using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// DEXA 설정(관리) 정적 페이지용 API. ServerConfig.razor 의 에이전트 목록 + 트리거 관리 이식.
/// - GET    /api/admin/config                       : 에이전트 목록 + 트리거 목록(+자산 매핑 수) 1회 조회.
/// - POST   /api/admin/config/triggers              : 트리거 추가 (ScheduleService.AddTriggerAsync 래핑).
/// - PUT    /api/admin/config/triggers/{id}/cron    : 스케줄(cron) 수정 (ScheduleService.UpdateTriggerCronAsync).
/// - POST   /api/admin/config/triggers/{id}/execute : 트리거 즉시 실행 (ScheduleService.ExecuteTriggerAsync).
/// - DELETE /api/admin/config/triggers/{id}         : 트리거 삭제 (ScheduleService.DeleteTriggerAsync).
/// 기존 서비스(DexaReadService / ScheduleService)를 얇게 래핑(신규 비즈니스 로직 없음).
/// 관리자 전용: 컨트롤러 레벨 [Authorize(Roles="Admin")] (GET 포함 — 민감 데이터/변경).
///
/// 이번 정적 포팅 제외(deviations):
/// - 에이전트 재시작 / 연결된 피어 조회: Akka.IActorRef(ActorInfo.ActorRef) 를 직접 다루므로
///   DexaServerClient.AskAsync(AmC2SRequestConnectedPeers/AmC2SRequestAgentRestart) 의존 → 깔끔한 서비스 메서드 없음.
/// - 트리거↔자산 매핑 편집(ScheduleAssetEditor 다이얼로그): 자산 선택 UI(트리)가 필요해 이번 범위 제외.
///   매핑 "개수"는 읽기 전용으로 표시.
/// </summary>
[ApiController]
[Route("api/admin/config")]
[Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
public class AdminConfigController : ControllerBase
{
    private readonly DexaReadService _dexaRead;
    private readonly ScheduleService _schedule;

    public AdminConfigController(DexaReadService dexaRead, ScheduleService schedule)
    {
        _dexaRead = dexaRead;
        _schedule = schedule;
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
}
