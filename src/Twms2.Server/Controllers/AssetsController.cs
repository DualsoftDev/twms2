using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Helpers;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Models.Dexa;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 자산 탐색기(AssetExplorer) + 자산 상세(AssetDetail) 정적 페이지용 스냅샷 API.
/// /assets 랜딩: 통합 자산 목록(KPI + 검색 가능 리스트).
/// /assets/{id}, /qr/{id} 상세: 기본정보/상태/매뉴얼/백업이력/관련링크.
/// 기존 AssetService / AssetStatusService / DexaReadService / ManualDbService 를
/// 얇게 래핑(신규 비즈니스 로직 없음). 모두 읽기 전용 — 편집은 Blazor 페이지 유지.
/// </summary>
[ApiController]
[Route("api/assets")]
// 공개 읽기(자산 조회/상세). 쓰기 없음(편집은 /api/assets/table 가 인증 요구).
public class AssetsController : ControllerBase
{
    private readonly AssetService _assets;
    private readonly AssetStatusService _status;
    private readonly DexaReadService _dexaRead;
    private readonly ManualDbService _manualDb;

    public AssetsController(
        AssetService assets,
        AssetStatusService status,
        DexaReadService dexaRead,
        ManualDbService manualDb)
    {
        _assets = assets;
        _status = status;
        _dexaRead = dexaRead;
        _manualDb = manualDb;
    }

    /// <summary>
    /// 랜딩(탐색기)용 통합 자산 목록 + KPI 요약. AssetExplorer.LoadAllAsync 의
    /// 실제 자산 목록(IsRealAsset)을 상태정보와 병합하여 제공.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var statuses = await _status.GetAssetStatusesAsync();

        var list = statuses
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                assetId = a.AssetId,
                name = a.Name,
                ip = a.Ip,
                typeName = a.AssetTypeName,
                typeId = a.AssetTypeId,
                lineName = a.LayoutLineName,
                isRobotPlc = a.AugIsRobotPLC is > 0,
                health = HealthKey(a.Health),
                healthLabel = LayoutHelpers.GetHealthLabel(a.Health),
                agentOnline = a.AgentOnline,
                lastBackupTime = a.LastBackupTime,
                pingReachable = a.LatestPing?.Reachable,
            })
            .ToList();

        // KPI (대시보드와 동일한 정의 — Home.RefreshAll)
        var total = statuses.Count;
        var backupSuccess = statuses.Count(s => s.LastBackupSucceeded);
        var changed = statuses.Count(s => s.LastBackupChanged == true);
        var noBackup = statuses.Count(s => s.LastBackupTime == null);
        var failed = total - backupSuccess - noBackup;
        var unchanged = backupSuccess - changed;
        var offline = statuses.Count(s => s.LatestPing is { Reachable: false });

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

        return Ok(new
        {
            assets = list,
            kpi = new { total, backedUp = changed, unchanged, failed, offline },
            typeNames,
            lineNames,
        });
    }

    /// <summary>
    /// 단일 자산 상세 스냅샷 (AssetDetail.razor 의 섹션을 1회 응답으로).
    /// 기본정보 + 상태 + 매뉴얼(매칭/전체) + 백업 이력 + 최신버전/마지막변경.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var assetsTask = _assets.GetAllAssetsAsync();
        var statusTask = _status.GetAssetStatusesAsync();
        var allActionsTask = _dexaRead.GetAllActionsAsync();
        var latestTask = _dexaRead.GetLatestActionPerAssetAsync();
        await Task.WhenAll(assetsTask, statusTask, allActionsTask, latestTask);

        var asset = assetsTask.Result.FirstOrDefault(a => a.IsRealAsset && a.AssetId == id);
        if (asset == null)
            return NotFound(new { message = "자산을 찾을 수 없습니다." });

        var statusInfo = statusTask.Result.FirstOrDefault(s => s.AssetId == id);

        // 매뉴얼 (AugSpec 키워드 매칭 + 전체)
        var matchedTask = _manualDb.GetManualsByKeywordMatchAsync(asset.AugSpec ?? "");
        var allManualsTask = _manualDb.GetAllManualsAsync();
        await Task.WhenAll(matchedTask, allManualsTask);

        // 이 자산의 백업 이력 (FillLastSuccessVersions 적용)
        var assetActions = allActionsTask.Result.Where(a => a.AssetId == id).ToList();
        FillLastSuccessVersions(assetActions);
        var typeId = asset.AssetTypeId ?? 0;
        bool hasReport = typeId is 4 or 6;

        var latest = latestTask.Result.FirstOrDefault(a => a.AssetId == id);
        int? latestVersion = latest?.Version;

        DateTime? lastChanged = assetActions
            .Where(a => a.ContentsChanged == true)
            .OrderByDescending(a => a.Finished)
            .FirstOrDefault()?.Finished;

        var backupHistory = assetActions
            .OrderByDescending(a => a.Started)
            .Select(a => new
            {
                id = a.Id,
                version = a.Version,
                started = a.Started,
                finished = a.Finished,
                result = GetResultKey(a),
                resultLabel = GetResultLabel(a),
                downloadableVersion = a.DownloadableVersion,
                contentsChanged = a.ContentsChanged,
                isSuccess = a.IsSuccess,
                isInProgress = IsInProgress(a),
                hasReport = a.ContentsChanged == true && hasReport,
            })
            .ToList();

        var matched = matchedTask.Result.Select(MapManual).ToList();
        var allManuals = allManualsTask.Result.Select(MapManual).ToList();

        var health = statusInfo?.Health ?? AssetHealthStatus.Unknown;

        return Ok(new
        {
            // ── 기본 정보 ──
            assetId = asset.AssetId,
            name = asset.DisplayName,
            typeName = asset.AssetTypeUserFriendlyName,
            typeId = asset.AssetTypeId,
            iconName = TypeIconName(asset),
            ip = asset.Ip,
            ipVia = asset.AugIpVia,
            viaEnabled = asset.ViaEnabled,
            vendor = asset.AugVendor,
            spec = asset.AugSpec,
            modelName = asset.ModelName,
            modelVersion = asset.ModelVersion,
            stationNumber = asset.AugStationNumber,
            baseNumber = asset.AugBaseNumber,
            slotNumber = asset.AugSlotNumber,
            isRobotPlc = asset.AugIsRobotPLC is > 0,
            lineName = asset.LayoutLineName,
            agent = asset.AssetAgentPreferences,
            description = asset.Description,

            // ── 상태 ──
            health = HealthKey(health),
            healthLabel = LayoutHelpers.GetHealthLabel(health),
            agentOnline = statusInfo?.AgentOnline ?? false,
            agentName = statusInfo?.AgentName,
            pingReachable = statusInfo?.LatestPing?.Reachable,
            pingRoundtripMs = statusInfo?.LatestPing?.RoundtripMs,
            pingCheckedAt = statusInfo?.LatestPing?.CheckedAt,

            // ── 백업 정보 ──
            latestVersion,
            lastBackupChangedTime = lastChanged,
            lastBackupTime = statusInfo?.LastBackupTime,
            backupHistory,

            // ── 매뉴얼 ──
            matchedManuals = matched,
            allManuals,
        });
    }

    private static object MapManual(Models.Twm.TwmsManual m) => new
    {
        keyword = m.Keyword,
        fileName = m.FileName,
        storedFileName = m.StoredFileName,
    };

    /// <summary>타입 아이콘 파일명 (AssetDetail.GetTypeIcon 이식). 빈 문자열이면 폴백 아이콘.</summary>
    private static string TypeIconName(ViewAsset a)
    {
        var n = a.AssetTypeUserFriendlyName;
        if (string.IsNullOrEmpty(n)) return "";
        if (n.Contains("PLC") && a.AugIsRobotPLC is > 0) return "robot.png";
        if (n.Contains("PLC")) return "plc.png";
        if (n.Contains("Servo")) return "servo.png";
        if (n.Contains("HMI") || n.Contains("XP")) return "hmi.png";
        if (n.Contains("Drive")) return "drive.png";
        if (n.Contains("FTP")) return "ftp.png";
        return "";
    }

    // ── HistoryController 와 동일한 라벨/판정 (이식) ──

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

    private static void FillLastSuccessVersions(List<DexaAction> actions)
    {
        int? lastSuccess = null;
        foreach (var action in actions.OrderBy(a => a.Id))
        {
            if (action.IsSuccess && action.Version.HasValue)
                lastSuccess = action.Version;
            else if (!action.IsSuccess)
                action.LastSuccessVersion = lastSuccess;
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
