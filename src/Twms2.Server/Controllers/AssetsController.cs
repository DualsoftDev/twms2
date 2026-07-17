using DEX.Core.Actor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Helpers;
using static Twms2.Server.Helpers.ActionResultHelper;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Models.Dexa;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 자산 탐색기(AssetExplorer) + 자산 상세(AssetDetail) 정적 페이지용 스냅샷 API.
/// /assets 랜딩: 통합 자산 목록(KPI + 검색 가능 리스트).
/// /assets/{id}, /qr/{id} 상세: 기본정보/상태/매뉴얼/백업이력/관련링크.
/// 기존 AssetService / AssetStatusService / DexaReadService / ManualDbService 를
/// 얇게 래핑(신규 비즈니스 로직 없음). 조회는 공개, 수동 백업 실행만 Admin.
/// </summary>
[ApiController]
[Route("api/assets")]
// 공개 읽기(자산 조회/상세) + Admin 전용 수동 백업 실행(편집은 /api/assets/table 가 인증 요구).
public class AssetsController : ControllerBase
{
    private readonly AssetService _assets;
    private readonly AssetStatusService _status;
    private readonly DexaReadService _dexaRead;
    private readonly ManualDbService _manualDb;
    private readonly PingDbService _pingDb;
    private readonly DexaServerClient _dexaClient;

    public AssetsController(
        AssetService assets,
        AssetStatusService status,
        DexaReadService dexaRead,
        ManualDbService manualDb,
        PingDbService pingDb,
        DexaServerClient dexaClient)
    {
        _assets = assets;
        _status = status;
        _dexaRead = dexaRead;
        _manualDb = manualDb;
        _pingDb = pingDb;
        _dexaClient = dexaClient;
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
                ipVia = a.AugIpVia,
                baseNumber = a.AugBaseNumber,
                slotNumber = a.AugSlotNumber,
                typeName = a.AssetTypeName,
                typeId = a.AssetTypeId,
                lineName = a.LayoutLineName,
                isRobotPlc = a.AugIsRobotPLC is > 0,
                health = LayoutHelpers.GetHealthKey(a.Health),
                healthLabel = LayoutHelpers.GetHealthLabel(a.Health),
                agentOnline = a.AgentOnline,
                agentName = a.AgentName,
                lastBackupTime = a.LastBackupTime,
                pingReachable = a.LatestPing?.Reachable,
            })
            .ToList();

        // KPI (단일 정의)
        var kpi = AssetStatusService.ComputeKpi(statuses);
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
            kpi = new { total = kpi.Total, backedUp = kpi.BackedUp, unchanged = kpi.Unchanged, failed = kpi.Failed, offline },
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

        // 마지막 온라인↔오프라인 상태 전환 (TwmsPingLog 는 전환 발생 시에만 기록됨)
        var lastPingChange = await _pingDb.GetLastPingChangeAsync(id);

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
            health = LayoutHelpers.GetHealthKey(health),
            healthLabel = LayoutHelpers.GetHealthLabel(health),
            agentOnline = statusInfo?.AgentOnline ?? false,
            agentName = statusInfo?.AgentName,
            pingReachable = statusInfo?.LatestPing?.Reachable,
            pingRoundtripMs = statusInfo?.LatestPing?.RoundtripMs,
            pingCheckedAt = statusInfo?.LatestPing?.CheckedAt,
            // 마지막 온라인/오프라인 전환 — 전환된 상태와 그 시각
            pingChangedReachable = lastPingChange?.Reachable,
            pingChangedAt = lastPingChange?.CheckedAt,

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

    /// <summary>
    /// 수동 백업 실행 (AssetExplorer.ExecuteManualBackup 이식 — fire &amp; forget).
    /// DEXA 는 완료 응답을 주지 않으므로 클라이언트는 backup-status 로 액션 행 등장/종료를 폴링한다.
    /// </summary>
    [HttpPost("{id:int}/backup")]
    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    public async Task<IActionResult> ExecuteBackup(int id)
    {
        if (!_dexaClient.IsConnected)
            return Conflict(new { error = "DEXA 서버에 연결되어 있지 않습니다." });

        var asset = (await _assets.GetAllAssetsAsync()).FirstOrDefault(a => a.IsRealAsset && a.AssetId == id);
        if (asset == null)
            return NotFound(new { error = "자산을 찾을 수 없습니다." });

        _dexaClient.Tell(new AmC2SRequestExecuteBackupOnce(id));
        return Accepted(new { requested = true });
    }

    /// <summary>
    /// 단일 자산의 최근 백업 액션 스냅샷(경량) — 수동 백업 진행 폴링용.
    /// 요청 직전 latestActionId 를 baseline 으로 잡고, 그보다 큰 id 의 새 행 등장 = 백업 시작,
    /// 그 행의 result != inprogress = 종료. (health 비교보다 견고 — 캐시 지연/동일상태 재백업에 오판 없음)
    /// </summary>
    [HttpGet("{id:int}/backup-status")]
    public async Task<IActionResult> GetBackupStatus(int id)
    {
        var actions = (await _dexaRead.GetAllActionsAsync())
            .Where(a => a.AssetId == id)
            .OrderByDescending(a => a.Id)
            .Take(5)
            .Select(a => new
            {
                id = a.Id,
                started = a.Started,
                finished = a.Finished,
                result = GetResultKey(a),
                resultLabel = GetResultLabel(a),
                contentsChanged = a.ContentsChanged,
                version = a.Version,
            })
            .ToList();

        return Ok(new
        {
            latestActionId = actions.Count > 0 ? actions[0].id : 0,
            actions,
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

}
