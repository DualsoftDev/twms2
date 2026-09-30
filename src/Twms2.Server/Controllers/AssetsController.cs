using DEX.Core.Actor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Helpers;
using static Twms2.Server.Helpers.ActionResultHelper;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Models.Dexa;
using Twms2.Server.Services;

using Twms2.Dexa;

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
    /// PLC(.xgwx) / 서보(.xpj) 프로젝트 파일 최대 크기.
    /// DEXA 의 Akka 프레임 상한(CommBufferSize 약 28MB)보다 넉넉히 아래로 잡는다.
    private const long MaxProjectFileSize = 20L * 1024 * 1024;

    /// <summary>
    /// PLC/서보 프로젝트 파일 업로드 (Admin 전용).
    /// 등록 API 에 넘길 토큰을 돌려준다. 파일은 등록이 확인될 때까지 임시 보관되고,
    /// 이탈해 남은 것은 유지보수 잡이 정리한다.
    /// </summary>
    [HttpPost("project-file")]
    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    [RequestSizeLimit(MaxProjectFileSize + 1024 * 1024)]
    public async Task<IActionResult> UploadProjectFile(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "파일을 선택해주세요." });
        if (file.Length > MaxProjectFileSize)
            return BadRequest(new { error = $"파일 크기가 {MaxProjectFileSize / (1024 * 1024)}MB 를 초과합니다." });

        var ext = Path.GetExtension(file.FileName);
        if (!string.Equals(ext, ".xgwx", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".xpj", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "PLC 는 .xgwx, 서보는 .xpj 파일만 올릴 수 있습니다." });

        // 원본 파일명을 그대로 보관한다 — DEXA projectFile.path 에 들어가고 화면에도 보인다.
        var safeName = Path.GetFileName(file.FileName);
        if (safeName.Any(Path.GetInvalidFileNameChars().Contains))
            return BadRequest(new { error = "파일명에 사용할 수 없는 문자가 있습니다." });

        Directory.CreateDirectory(AssetService.ProjectTempDir);
        var token = Guid.NewGuid().ToString("N");
        var path = Path.Combine(AssetService.ProjectTempDir, $"{token}__{safeName}");

        await using (var fs = System.IO.File.Create(path))
            await file.CopyToAsync(fs);

        var md5 = AssetService.Md5Hex(await System.IO.File.ReadAllBytesAsync(path));

        // 같은 파일이 이미 다른 자산에 쓰이고 있으면 복붙 실수일 가능성이 높다 — 경고만 하고 막지는 않는다.
        var used = await _dexaRead.GetProjectFileChecksumsAsync();
        var warning = used.TryGetValue(md5, out var owner) && !string.IsNullOrEmpty(owner)
            ? $"같은 프로젝트 파일이 이미 '{owner}' 에 사용 중입니다. 파일을 잘못 고르지 않았는지 확인하세요."
            : null;

        return Ok(new { ok = true, token, fileName = safeName, size = file.Length, md5, warning });
    }

    /// <summary>
    /// 자산 신규 등록 (Admin 전용). DEXA 서버 경유(CreateNewAsset) — 상위 폴더부터 만든다.
    /// 경로 규칙: /twms/{라인ID}_{라인명}/{타입폴더}/{자산명}
    /// </summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    public async Task<IActionResult> Register([FromBody] AssetService.RegisterAssetRequest req)
    {
        if (req is null)
            return BadRequest(new { error = "요청 본문이 비어 있습니다." });
        if (!_dexaClient.IsConnected)
            return StatusCode(503, new { error = "DEXA 서버에 연결되어 있지 않습니다. 등록은 서버가 필요합니다." });

        // 드라이브 모델 버전은 백업에 쓸 파라미터 맵을 고르는 값이다. 비워두면 템플릿 기본값(1.00)이
        // 그대로 남는데, 그렇게 만들어진 백업은 실패하지 않고 조용히 어긋나므로 여기서 막는다.
        if (req.AssetTypeId == AssetService.DriveTypeId && string.IsNullOrWhiteSpace(req.ModelVersion))
            return BadRequest(new { error = "드라이브는 모델 버전이 필요합니다." });

        try
        {
            var (ok, error, assetId) = await _assets.RegisterAssetAsync(req);
            return ok ? Ok(new { ok = true, assetId }) : BadRequest(new { error });
        }
        catch (DexaServerException ex)
        {
            // 서버가 거부한 사유를 그대로 보여준다(라이선스 만료·이름 중복·서버 Lock).
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 자산/폴더 삭제 (Admin 전용). DEXA 서버 경유 — soft delete 이고 자식은 직접 순회한다.
    /// 백업 파일·이력은 보존되며 라이선스 슬롯은 반환된다.
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool includeChildren = false)
    {
        if (!_dexaClient.IsConnected)
            return StatusCode(503, new { error = "DEXA 서버에 연결되어 있지 않습니다. 삭제는 서버가 필요합니다." });

        try
        {
            var (ok, error, deleted) = await _assets.DeleteAssetAsync(id, includeChildren);
            return ok ? Ok(new { ok = true, deleted }) : BadRequest(new { error, deleted });
        }
        catch (DexaServerException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>선택 삭제 요청. 표에서 체크한 행들의 자산 id.</summary>
    public record DeleteAssetsRequest(int[] AssetIds, bool IncludeChildren = false);

    /// <summary>
    /// 자산 여러 건 삭제 (Admin 전용). 건별로 진행하며 중간에 실패해도 나머지를 계속한다 —
    /// DEXA 삭제는 건마다 개별 커밋이라 전체를 되돌릴 수 없고, 되돌리는 척하는 편이 더 위험하다.
    /// 그래서 전체 성공/실패 대신 건별 결과를 돌려준다.
    /// </summary>
    [HttpPost("delete")]
    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    public async Task<IActionResult> DeleteMany([FromBody] DeleteAssetsRequest req)
    {
        if (req?.AssetIds is null || req.AssetIds.Length == 0)
            return BadRequest(new { error = "삭제할 자산을 선택해주세요." });
        if (!_dexaClient.IsConnected)
            return StatusCode(503, new { error = "DEXA 서버에 연결되어 있지 않습니다. 삭제는 서버가 필요합니다." });

        try
        {
            var results = await _assets.DeleteAssetsAsync(req.AssetIds, req.IncludeChildren);
            return Ok(new
            {
                ok      = results.All(r => r.Ok),
                success = results.Count(r => r.Ok),
                fail    = results.Count(r => !r.Ok),
                results = results.Select(r => new { assetId = r.AssetId, success = r.Ok, error = r.Error }),
            });
        }
        catch (DexaServerException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

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
