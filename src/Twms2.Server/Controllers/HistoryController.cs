using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Helpers;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Models.Dexa;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 자산 통합조회(History) 정적 페이지용 스냅샷 API. ActionHistory.razor 의 3개 탭
/// (자산 정보 / 백업 이력 / 통신 이력) 데이터를 제공.
/// 기존 DexaReadService / AssetStatusService / PingDbService 를 얇게 래핑(신규 비즈니스 로직 없음).
/// </summary>
[ApiController]
[Route("api/history")]
// 공개 읽기(조회 페이지). 쓰기 없음.
public class HistoryController : ControllerBase
{
    private readonly DexaReadService _dexaRead;
    private readonly AssetStatusService _status;
    private readonly PingDbService _pingDb;

    public HistoryController(DexaReadService dexaRead, AssetStatusService status, PingDbService pingDb)
    {
        _dexaRead = dexaRead;
        _status = status;
        _pingDb = pingDb;
    }

    /// <summary>
    /// 탭1(자산 정보) + 탭2(백업 이력) 스냅샷. 통신 이력은 기간이 필요하므로 /api/history/pings 로 분리.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var assetsTask = _dexaRead.GetViewAssetsAsync();
        var actionsTask = _dexaRead.GetAllActionsAsync();
        var statusTask = _status.GetAssetStatusesAsync();
        await Task.WhenAll(assetsTask, actionsTask, statusTask);

        var viewAssets = assetsTask.Result;
        var allActions = actionsTask.Result;
        var statuses = statusTask.Result;

        // 실패 액션에 직전 성공 버전 채움 (Razor.FillLastSuccessVersions 이식)
        FillLastSuccessVersions(allActions);

        // assetId → 표시정보 매핑 (백업/통신 이력 행의 자산명/타입/라인/IP 표기에 사용)
        // 이름/타입/IP 는 ViewAsset 기준(Razor 와 동일), 라인은 status 기준.
        var nameMap = viewAssets.ToDictionary(a => a.AssetId, a => a.DisplayName);
        var typeMap = viewAssets.ToDictionary(a => a.AssetId, a => a.AssetTypeUserFriendlyName ?? "");
        var typeIdMap = viewAssets.ToDictionary(a => a.AssetId, a => a.AssetTypeId);
        var ipMap = viewAssets.ToDictionary(a => a.AssetId, a => a.Ip ?? "");
        var lineMap = statuses
            .Where(a => !string.IsNullOrEmpty(a.LayoutLineName))
            .ToDictionary(a => a.AssetId, a => a.LayoutLineName!);

        string NameOf(int id) => nameMap.TryGetValue(id, out var v) ? v : $"#{id}";
        string TypeOf(int id) => typeMap.TryGetValue(id, out var v) ? v : "";
        string LineOf(int id) => lineMap.TryGetValue(id, out var v) ? v : "";
        string IpOf(int id) => ipMap.TryGetValue(id, out var v) ? v : "";

        // ── 탭1: 자산 정보 ──
        var assets = statuses
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                assetId = a.AssetId,
                name = a.Name,
                ip = a.Ip,
                typeName = a.AssetTypeName,
                typeId = a.AssetTypeId,
                lineName = a.LayoutLineName,
                vendor = a.AugVendor,
                spec = a.AugSpec,
                description = a.Description,
                ipVia = a.AugIpVia,
                baseNumber = a.AugBaseNumber,
                slotNumber = a.AugSlotNumber,
                stationNumber = a.AugStationNumber,
                modelVersion = a.ModelVersion,
                isRobotPlc = a.AugIsRobotPLC == 1,
                health = HealthKey(a.Health),
                healthLabel = LayoutHelpers.GetHealthLabel(a.Health),
                agentOnline = a.AgentOnline,
                lastBackupTime = a.LastBackupTime,
                pingReachable = a.LatestPing?.Reachable,
                pingRoundtripMs = a.LatestPing?.RoundtripMs,
            })
            .ToList();

        // 필터 드롭다운 옵션
        var typeNames = statuses
            .Select(a => a.AssetTypeName)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .OrderBy(t => t)
            .ToList();
        var lineNames = statuses
            .Select(a => a.LayoutLineName)
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct()
            .OrderBy(l => l)
            .ToList();

        // ── 탭2: 백업 이력 (전체, 기간 필터는 클라이언트가 started 로 수행) ──
        var actions = allActions
            .Select(a => new
            {
                id = a.Id,
                assetId = a.AssetId,
                assetName = NameOf(a.AssetId),
                typeName = TypeOf(a.AssetId),
                lineName = LineOf(a.AssetId),
                version = a.Version,
                started = a.Started,
                finished = a.Finished,
                result = GetResultKey(a),
                resultLabel = GetResultLabel(a),
                downloadableVersion = a.DownloadableVersion,
                contentsChanged = a.ContentsChanged,
                isSuccess = a.IsSuccess,
                isInProgress = IsInProgress(a),
                isIncomplete = a.IsIncomplete,
                hasReport = a.ContentsChanged == true && HasReport(typeIdMap, a.AssetId),
            })
            .ToList();

        return Ok(new
        {
            assets,
            actions,
            typeNames,
            lineNames,
            // 통신 이력 행 표기에 사용할 assetId → IP 맵 (탭3 행에서 IP 표시)
            assetMeta = statuses.ToDictionary(
                a => a.AssetId.ToString(),
                a => new { name = a.Name, typeName = a.AssetTypeName ?? "", lineName = a.LayoutLineName ?? "", ip = a.Ip ?? "" }),
            today = DateTime.Today.ToString("yyyy-MM-dd"),
        });
    }

    /// <summary>
    /// 탭3: 통신 이력(온라인↔오프라인 전환 로그). 기간 지정 필수(?start=&end=, yyyy-MM-dd).
    /// 미지정 시 오늘. PingDbService.GetPingLogsAsync 와 동일하게 end 는 포함(자정까지).
    /// </summary>
    [HttpGet("pings")]
    public async Task<IActionResult> GetPings([FromQuery] string? start, [FromQuery] string? end)
    {
        var startDate = DateTime.TryParse(start, out var s) ? s.Date : DateTime.Today;
        var endDate = DateTime.TryParse(end, out var e) ? e.Date : DateTime.Today;

        var logs = await _pingDb.GetPingLogsAsync(startDate, endDate);

        var rows = logs
            .Select(p => new
            {
                id = p.Id,
                assetId = p.DexaAssetId,
                reachable = p.Reachable,
                checkedAt = p.CheckedAt,
            })
            .ToList();

        return Ok(new { pings = rows });
    }

    // ── Razor 로직 이식 (라벨/판정) ──

    private static bool IsInProgress(DexaAction a) => a.IsInProgress && !a.IsIncomplete;

    private static string GetResultKey(DexaAction a)
        => IsInProgress(a) ? "inprogress"
         : !a.IsSuccess ? (a.IsIncomplete ? "incomplete" : "failed")
         : a.ContentsChanged == true ? "backedup"
         : "unchanged";

    private static string GetResultLabel(DexaAction a)
        => IsInProgress(a) ? "작업중"
         : !a.IsSuccess ? (a.IsIncomplete ? "미완료" : "작업 실패")
         : a.ContentsChanged == true ? "백업 갱신"
         : "변경 없음";

    /// <summary>리포트 버튼 표시 여부: PLC(6)와 Drive/인버터(4)만 리포트 지원 (Razor.HasReport 이식)</summary>
    private static bool HasReport(Dictionary<int, int?> typeIdMap, int assetId)
        => typeIdMap.TryGetValue(assetId, out var typeId) && (typeId == 4 || typeId == 6);

    private static void FillLastSuccessVersions(List<DexaAction> actions)
    {
        foreach (var group in actions.GroupBy(a => a.AssetId))
        {
            int? lastSuccess = null;
            foreach (var action in group.OrderBy(a => a.Id))
            {
                if (action.IsSuccess && action.Version.HasValue)
                    lastSuccess = action.Version;
                else if (!action.IsSuccess)
                    action.LastSuccessVersion = lastSuccess;
            }
        }
    }

    private static string HealthKey(AssetHealthStatus h) => h switch
    {
        AssetHealthStatus.BackedUp => "backedup",
        AssetHealthStatus.Unchanged => "unchanged",
        AssetHealthStatus.Failed => "failed",
        AssetHealthStatus.InProgress => "inprogress",
        _ => "unknown",
    };
}
