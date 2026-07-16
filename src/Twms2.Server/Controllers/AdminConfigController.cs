using Akka.Actor;
using DEX.Core.Actor;
using DEX.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Helpers;
using Twms2.Server.Models.Dexa;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// DEXA 설정(관리) 정적 페이지용 API. ServerConfig.razor 의 에이전트 목록 + 트리거 관리 이식.
/// - GET    /api/admin/config                       : 에이전트 목록 + 트리거 목록(+자산 매핑 수) 1회 조회.
/// - POST   /api/admin/config/triggers              : 트리거 추가 (ScheduleService.AddTriggerAsync 래핑).
/// - PUT    /api/admin/config/triggers/{id}/cron    : 스케줄(cron) 수정 (ScheduleService.UpdateTriggerCronAsync).
/// - PUT    /api/admin/config/triggers/{id}/name    : 트리거 이름 변경 (ScheduleService.UpdateTriggerNameAsync).
/// - POST   /api/admin/config/triggers/{id}/execute : 트리거 즉시 실행 (ScheduleService.ExecuteTriggerAsync).
/// - DELETE /api/admin/config/triggers/{id}         : 트리거 삭제 (ScheduleService.DeleteTriggerAsync).
/// - GET    /api/admin/config/triggers/{id}/assets : 트리거 자산 매핑 조회(선택ID + 라인별 자산).
/// - PUT    /api/admin/config/triggers/{id}/assets : 트리거↔자산 매핑 저장 (ScheduleService.UpdateSchedulesAsync).
/// - POST   /api/admin/config/agents/{id}/restart  : 에이전트 재시작 (ServerConfig.RestartAgent 이식 — 피어 조회 후 Akka Ask).
/// 기존 서비스(DexaReadService / ScheduleService / AssetService / DexaServerClient)를 얇게 래핑(신규 비즈니스 로직 없음).
/// 관리자 전용: 컨트롤러 레벨 [Authorize(Roles="Admin")] (GET 포함 — 민감 데이터/변경).
/// </summary>
[ApiController]
[Route("api/admin/config")]
[Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
public class AdminConfigController : ControllerBase
{
    private readonly DexaReadService _dexaRead;
    private readonly ScheduleService _schedule;
    private readonly DexaServerClient _server;
    private readonly AssetService _assets;

    public AdminConfigController(DexaReadService dexaRead, ScheduleService schedule, DexaServerClient server, AssetService assets)
    {
        _dexaRead = dexaRead;
        _schedule = schedule;
        _server = server;
        _assets = assets;
    }

    // ──────────────── 조회 ────────────────

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var agentsTask = _dexaRead.GetAgentsAsync();
        var triggersTask = _schedule.GetTriggersAsync();
        var schedulesTask = _schedule.GetSchedulesAsync();
        await Task.WhenAll(agentsTask, triggersTask, schedulesTask);

        var agents = agentsTask.Result
            .Select(a => new
            {
                id = a.Id,
                name = a.Name,
                ip = a.Ip,
                swVersion = a.SwVersion,
                online = a.Online,
                connected = a.Connected,
                disconnected = a.Disconnected,
            })
            .ToList();

        var schedules = schedulesTask.Result;
        var triggers = triggersTask.Result
            .OrderBy(t => t.Id)
            .Select(t => new
            {
                id = t.Id,
                name = t.Name,
                cronExpression = t.CronExpression,
                enabled = t.Enabled,
                description = t.Description,
                mappedAssetCount = schedules.Count(s => s.TriggerId == t.Id),
            })
            .ToList();

        return Ok(new
        {
            agents,
            triggers,
        });
    }

    // ──────────────── 트리거 추가 ────────────────

    public record TriggerCreateDto(string? Name, string? CronExpression, string? Description);

    [HttpPost("triggers")]
    public async Task<IActionResult> AddTrigger([FromBody] TriggerCreateDto dto)
    {
        var name = (dto.Name ?? "").Trim();
        var cron = (dto.CronExpression ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "트리거 이름을 입력해주세요." });
        if (string.IsNullOrWhiteSpace(cron))
            return BadRequest(new { error = "Cron 표현식을 입력해주세요." });

        var ok = await _schedule.AddTriggerAsync(name, cron, string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim());
        if (!ok) return StatusCode(502, new { error = "트리거 추가에 실패했습니다." });
        return Ok(new { ok = true });
    }

    // ──────────────── 스케줄(cron) 수정 ────────────────

    public record CronDto(string? CronExpression);

    [HttpPut("triggers/{id:int}/cron")]
    public async Task<IActionResult> UpdateCron(int id, [FromBody] CronDto dto)
    {
        var cron = (dto.CronExpression ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cron))
            return BadRequest(new { error = "Cron 표현식을 입력해주세요." });

        var ok = await _schedule.UpdateTriggerCronAsync(id, cron);
        if (!ok) return StatusCode(502, new { error = "스케줄 수정에 실패했습니다." });
        return Ok(new { ok = true });
    }

    // ──────────────── 트리거 이름 변경 ────────────────

    public record NameDto(string? Name);

    [HttpPut("triggers/{id:int}/name")]
    public async Task<IActionResult> UpdateName(int id, [FromBody] NameDto dto)
    {
        var name = (dto.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "트리거 이름을 입력해주세요." });

        var ok = await _schedule.UpdateTriggerNameAsync(id, name);
        if (!ok) return StatusCode(502, new { error = "트리거 이름 변경에 실패했습니다." });
        return Ok(new { ok = true });
    }

    // ──────────────── 트리거 즉시 실행 ────────────────

    [HttpPost("triggers/{id:int}/execute")]
    public async Task<IActionResult> ExecuteTrigger(int id)
    {
        var ok = await _schedule.ExecuteTriggerAsync(id);
        if (!ok) return StatusCode(502, new { error = "트리거 실행 요청에 실패했습니다." });
        return Ok(new { ok = true });
    }

    // ──────────────── 트리거 삭제 ────────────────

    [HttpDelete("triggers/{id:int}")]
    public async Task<IActionResult> DeleteTrigger(int id)
    {
        var ok = await _schedule.DeleteTriggerAsync(id);
        if (!ok) return StatusCode(502, new { error = "트리거 삭제에 실패했습니다." });
        return Ok(new { ok = true });
    }

    // ──────────────── 트리거 ↔ 자산 매핑 (ScheduleAssetEditor 이식) ────────────────

    /// <summary>트리거에 매핑된 자산 ID + 선택용 라인별 자산 목록 (실제 자산만).</summary>
    [HttpGet("triggers/{id:int}/assets")]
    public async Task<IActionResult> GetTriggerAssets(int id)
    {
        var assetsTask = _assets.GetAllAssetsAsync();
        var schedulesTask = _schedule.GetSchedulesAsync();
        await Task.WhenAll(assetsTask, schedulesTask);

        var selectedIds = schedulesTask.Result
            .Where(s => s.TriggerId == id)
            .Select(s => s.AssetId)
            .Distinct()
            .ToList();

        // 라인별 그룹핑 (ScheduleAssetEditor.OnInitializedAsync 와 동일: 실제 자산만, '라인없음' 후순위)
        var groups = assetsTask.Result
            .Where(a => a.IsRealAsset)
            .GroupBy(a => a.LayoutLineName ?? "라인없음")
            .OrderBy(g => g.Key == "라인없음" ? 1 : 0)
            .ThenBy(g => g.Key)
            .Select(g => new
            {
                lineName = g.Key,
                assets = g.OrderBy(a => a.DisplayName).Select(a => new
                {
                    assetId = a.AssetId,
                    name = a.DisplayName,
                    ip = a.Ip,
                    // GetAssetIcon 은 "images/icons/..." 상대경로 → 정적 페이지용 절대경로로 변환
                    icon = "/" + LayoutHelpers.GetAssetIcon(a.AssetTypeUserFriendlyName, a.AugIsRobotPLC),
                }).ToList(),
            })
            .ToList();

        return Ok(new { selectedIds, groups });
    }

    public record AssetMapDto(int[]? AssetIds);

    [HttpPut("triggers/{id:int}/assets")]
    public async Task<IActionResult> SaveTriggerAssets(int id, [FromBody] AssetMapDto dto)
    {
        var ids = dto?.AssetIds ?? [];
        var ok = await _schedule.UpdateSchedulesAsync(id, ids);
        if (!ok) return StatusCode(502, new { error = "자산 매핑 저장에 실패했습니다." });
        return Ok(new { ok = true, count = ids.Length });
    }

    // ──────────────── 에이전트 재시작 (ServerConfig.RestartAgent 이식) ────────────────

    [HttpPost("agents/{id:int}/restart")]
    public async Task<IActionResult> RestartAgent(int id)
    {
        var agents = await _dexaRead.GetAgentsAsync();
        var agent = agents.FirstOrDefault(a => a.Id == id);
        if (agent == null)
            return NotFound(new { error = "에이전트를 찾을 수 없습니다." });
        if (!agent.Online)
            return Conflict(new { error = "오프라인 에이전트는 재시작할 수 없습니다." });

        // 연결된 피어(Akka ActorRef) 조회 후 이름으로 매칭 (FindConnectedPeer 이식)
        ActorInfo? peer;
        try
        {
            var reply = await _server.AskAsync<AmS2CReplyConnectedPeers>(new AmC2SRequestConnectedPeers());
            peer = reply?.Peers?
                .Where(p => p.ActorType == ActorType.Agent)
                .FirstOrDefault(p => string.Equals(p.Name, agent.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"DEXA 서버 연결 실패: {ex.Message}" });
        }

        if (peer == null)
            return Conflict(new { error = "연결된 피어 정보가 없어 재시작할 수 없습니다." });

        try
        {
            var request = new AmC2SRequestAgentRestart { Agent = peer.ActorRef };
            await _server.AskAsync<AmS2CAgentShutdown>(request);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"재시작 요청 실패: {ex.Message}" });
        }
    }
}
