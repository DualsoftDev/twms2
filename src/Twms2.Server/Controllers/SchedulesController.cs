using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 스케줄(트리거) 관리 정적 페이지용 API. ServerConfig.razor 의 "트리거 목록" 위젯
/// (트리거 추가 / 스케줄(cron) 수정 / 자산 매핑 / 실행 / 삭제) 을 정적 페이지로 이식.
/// 기존 ScheduleService / AssetService 를 얇게 래핑(신규 비즈니스 로직 없음).
/// 원본 ScheduleList.razor 가 [Authorize(Roles="Admin")] 였으므로 Admin 전용으로 보호.
/// </summary>
[ApiController]
[Route("api/schedules")]
[Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
public class SchedulesController : ControllerBase
{
    private readonly ScheduleService _schedule;
    private readonly AssetService _assets;

    public SchedulesController(ScheduleService schedule, AssetService assets)
    {
        _schedule = schedule;
        _assets = assets;
    }

    // ──────────────── 조회 ────────────────

    /// <summary>트리거 목록 + 트리거별 매핑 자산 수 + 매핑 편집용 라인별 자산 그룹.</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var triggersTask = _schedule.GetTriggersAsync();
        var schedulesTask = _schedule.GetSchedulesAsync();
        var assetsTask = _assets.GetAllAssetsAsync();
        await Task.WhenAll(triggersTask, schedulesTask, assetsTask);

        var triggers = triggersTask.Result;
        var schedules = schedulesTask.Result;
        var assets = assetsTask.Result;

        // 트리거별 매핑된 자산 ID 집합 (자산 매핑 카운트 + 편집 초기 선택값)
        var mapByTrigger = schedules
            .GroupBy(s => s.TriggerId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.AssetId).Distinct().ToList());

        var triggerRows = triggers
            .Select(t =>
            {
                var ids = mapByTrigger.TryGetValue(t.Id, out var list) ? list : new List<int>();
                return new
                {
                    id = t.Id,
                    name = t.Name,
                    cronExpression = t.CronExpression,
                    enabled = t.Enabled,
                    description = t.Description,
                    assetCount = ids.Count,
                    assetIds = ids,
                };
            })
            .ToList();

        // 매핑 편집기: 실제 자산만 라인별 그룹핑 (ScheduleAssetEditor.razor 와 동일)
        var assetGroups = assets
            .Where(a => a.IsRealAsset)
            .GroupBy(a => a.LayoutLineName ?? "라인없음")
            .OrderBy(g => g.Key == "라인없음" ? 1 : 0)
            .ThenBy(g => g.Key)
            .Select(g => new
            {
                lineName = g.Key,
                assets = g.OrderBy(a => a.DisplayName)
                    .Select(a => new
                    {
                        assetId = a.AssetId,
                        displayName = a.DisplayName,
                        ip = a.Ip,
                        typeName = a.AssetTypeUserFriendlyName,
                    })
                    .ToList(),
            })
            .ToList();

        return Ok(new
        {
            triggers = triggerRows,
            assetGroups,
        });
    }

    // ──────────────── 트리거 추가 ────────────────

    public record AddTriggerDto(string? Name, string? CronExpression, string? Description);

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddTriggerDto dto)
    {
        var name = (dto.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "트리거 이름을 입력해주세요." });

        var cron = string.IsNullOrWhiteSpace(dto.CronExpression) ? "0 0 * * * ?" : dto.CronExpression.Trim();
        var ok = await _schedule.AddTriggerAsync(name, cron, dto.Description);
        if (!ok) return StatusCode(502, new { error = "트리거 추가에 실패했습니다." });
        return Ok(new { ok = true });
    }

    // ──────────────── 스케줄(cron) 수정 ────────────────

    public record CronDto(string? CronExpression);

    [HttpPut("{id:int}/cron")]
    public async Task<IActionResult> UpdateCron(int id, [FromBody] CronDto dto)
    {
        var cron = (dto.CronExpression ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cron))
            return BadRequest(new { error = "Cron 표현식을 입력해주세요." });

        var ok = await _schedule.UpdateTriggerCronAsync(id, cron);
        if (!ok) return StatusCode(502, new { error = "스케줄 수정에 실패했습니다." });
        return Ok(new { ok = true });
    }

    // ──────────────── 자산 매핑 수정 ────────────────

    public record MappingDto(int[]? AssetIds);

    [HttpPut("{id:int}/assets")]
    public async Task<IActionResult> UpdateMapping(int id, [FromBody] MappingDto dto)
    {
        var ok = await _schedule.UpdateSchedulesAsync(id, dto.AssetIds ?? []);
        if (!ok) return StatusCode(502, new { error = "자산 매핑 저장에 실패했습니다." });
        return Ok(new { ok = true });
    }

    // ──────────────── 즉시 실행 (1회) ────────────────

    [HttpPost("{id:int}/execute")]
    public async Task<IActionResult> Execute(int id)
    {
        var ok = await _schedule.ExecuteTriggerAsync(id);
        if (!ok) return StatusCode(502, new { error = "트리거 실행 요청에 실패했습니다." });
        return Ok(new { ok = true });
    }

    // ──────────────── 삭제 ────────────────

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ok = await _schedule.DeleteTriggerAsync(id);
        if (!ok) return StatusCode(502, new { error = "트리거 삭제에 실패했습니다." });
        return Ok(new { ok = true });
    }
}
