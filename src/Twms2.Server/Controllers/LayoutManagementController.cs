using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Models.Twm;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 레이아웃 관리(Admin/LayoutManagement.razor) 정적 페이지용 스냅샷 API.
/// 레이아웃 목록(도면 썸네일 포함) + 생성/복제/이름변경/순서변경/삭제.
/// 기존 LayoutDbService 의 공개 메서드만 얇게 래핑(신규 비즈니스 로직 없음).
/// Admin 전용. 편집기(/admin/layout/{id}) 자체는 Blazor 페이지 유지.
/// </summary>
[ApiController]
[Route("api/admin/layout")]
[Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
public class LayoutManagementController : ControllerBase
{
    private readonly LayoutDbService _layoutDb;

    public LayoutManagementController(LayoutDbService layoutDb)
    {
        _layoutDb = layoutDb;
    }

    /// <summary>
    /// 레이아웃 목록 + 레이아웃별 도면 썸네일 경로. (LayoutManagement.ReloadAsync 이식)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var layouts = await _layoutDb.GetAllLayoutsAsync();

        var list = new List<object>(layouts.Count);
        foreach (var layout in layouts)
        {
            var cfg = await _layoutDb.GetBlueprintConfigAsync(layout.Id);
            var imagePath = string.IsNullOrEmpty(cfg?.ImagePath)
                ? null
                : "/" + cfg!.ImagePath.TrimStart('/');
            list.Add(new
            {
                id = layout.Id,
                name = layout.Name,
                sortOrder = layout.SortOrder,
                updatedAt = layout.UpdatedAt == default ? (DateTime?)null : layout.UpdatedAt,
                imagePath,
            });
        }

        return Ok(new { layouts = list });
    }

    // ──────────────── 생성 ────────────────

    public record CreateDto(string? Name);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDto dto)
    {
        var name = (dto?.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "레이아웃 이름을 입력하세요." });

        var newId = await _layoutDb.InsertLayoutAsync(new TwmsLayout { Name = name });
        return Ok(new { ok = true, id = newId });
    }

    // ──────────────── 복제 ────────────────

    public record DuplicateDto(string? Name);

    [HttpPost("{id:int}/duplicate")]
    public async Task<IActionResult> Duplicate(int id, [FromBody] DuplicateDto dto)
    {
        var name = (dto?.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "복제본 이름을 입력하세요." });

        var newId = await _layoutDb.DuplicateLayoutAsync(id, name);
        return Ok(new { ok = true, id = newId });
    }

    // ──────────────── 이름 변경 ────────────────

    public record RenameDto(string? Name);

    [HttpPut("{id:int}/name")]
    public async Task<IActionResult> Rename(int id, [FromBody] RenameDto dto)
    {
        var name = (dto?.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "새 이름을 입력하세요." });

        var layouts = await _layoutDb.GetAllLayoutsAsync();
        var layout = layouts.FirstOrDefault(l => l.Id == id);
        if (layout == null)
            return NotFound(new { error = "레이아웃을 찾을 수 없습니다." });

        layout.Name = name;
        await _layoutDb.UpdateLayoutAsync(layout);
        return Ok(new { ok = true });
    }

    // ──────────────── 순서 변경 ────────────────

    public record ReorderDto(int[]? Ids);

    [HttpPut("reorder")]
    public async Task<IActionResult> Reorder([FromBody] ReorderDto dto)
    {
        var ids = dto?.Ids ?? [];
        if (ids.Length == 0)
            return BadRequest(new { error = "정렬할 레이아웃이 없습니다." });

        var sortOrders = ids.Select((id, i) => (Id: id, SortOrder: i)).ToList();
        await _layoutDb.UpdateLayoutSortOrdersAsync(sortOrders);
        return Ok(new { ok = true });
    }

    // ──────────────── 삭제 ────────────────

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var layouts = await _layoutDb.GetAllLayoutsAsync();
        if (layouts.Count <= 1)
            return BadRequest(new { error = "마지막 레이아웃은 삭제할 수 없습니다." });
        if (layouts.All(l => l.Id != id))
            return NotFound(new { error = "레이아웃을 찾을 수 없습니다." });

        await _layoutDb.DeleteLayoutAsync(id);
        return Ok(new { ok = true });
    }
}
