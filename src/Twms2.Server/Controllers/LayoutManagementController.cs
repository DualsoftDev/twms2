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
    private readonly AssetStatusService _status;

    public LayoutManagementController(LayoutDbService layoutDb, AssetStatusService status)
    {
        _layoutDb = layoutDb;
        _status = status;
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

    // ──────────────── JSON Export / Import / 도면 이미지 다운로드 ────────────────
    // LayoutManagement.razor 의 ExportLayoutAsync / OnImportFileSelected / DownloadImageAsync 이식.
    // 직렬화 옵션은 Blazor 원본과 동일(camelCase + indented)해야 export↔import 라운드트립이 호환됨.

    private static readonly System.Text.Json.JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    private static readonly System.Text.Json.JsonSerializerOptions ImportJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    [HttpGet("{id:int}/export")]
    public async Task<IActionResult> Export(int id)
    {
        var layouts = await _layoutDb.GetAllLayoutsAsync();
        var layout = layouts.FirstOrDefault(l => l.Id == id);
        if (layout == null)
            return NotFound(new { error = "레이아웃을 찾을 수 없습니다." });

        var data = await _layoutDb.ExportLayoutAsync(id, layout.Name);
        var json = System.Text.Json.JsonSerializer.Serialize(data, ExportJsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var fileName = $"layout-{Sanitize(layout.Name)}-{DateTime.Now:yyyyMMdd-HHmm}.json";
        return File(bytes, "application/json", fileName);
    }

    private const long MaxImportSize = 10L * 1024 * 1024; // 10MB (Blazor OnImportFileSelected 와 동일)

    [HttpPost("{id:int}/import")]
    [RequestSizeLimit(MaxImportSize + 1024 * 1024)]
    public async Task<IActionResult> Import(int id, IFormFile? file)
    {
        var layouts = await _layoutDb.GetAllLayoutsAsync();
        if (layouts.All(l => l.Id != id))
            return NotFound(new { error = "레이아웃을 찾을 수 없습니다." });
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "JSON 파일을 선택해주세요." });
        if (file.Length > MaxImportSize)
            return BadRequest(new { error = "파일 크기가 10MB를 초과합니다." });

        LayoutExportData? data;
        try
        {
            await using var stream = file.OpenReadStream();
            data = await System.Text.Json.JsonSerializer.DeserializeAsync<LayoutExportData>(stream, ImportJsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequest(new { error = "JSON 형식이 올바르지 않습니다. 레이아웃 내보내기 파일인지 확인하세요." });
        }

        if (data == null || data.Version < 1)
            return BadRequest(new { error = "유효하지 않은 JSON 파일입니다." });
        if (data.Positions.Count == 0 && data.Groups.Count == 0 && data.BlueprintRects.Count == 0)
            return BadRequest(new { error = "가져올 데이터가 없는 JSON 파일입니다." });

        // 유효 자산 ID (LayoutManagement.OnImportFileSelected 와 동일 — 현재 자산만 통과, 나머지 skip)
        var statuses = await _status.GetAssetStatusesAsync();
        var validIds = statuses.Select(a => a.AssetId).ToHashSet();

        var (positions, groups, rects, skipped) = await _layoutDb.ImportLayoutAsync(id, data, validIds);
        return Ok(new { ok = true, positions, groups, rects, skipped });
    }

    [HttpGet("{id:int}/image")]
    public async Task<IActionResult> DownloadImage(int id)
    {
        var layouts = await _layoutDb.GetAllLayoutsAsync();
        var layout = layouts.FirstOrDefault(l => l.Id == id);
        if (layout == null)
            return NotFound(new { error = "레이아웃을 찾을 수 없습니다." });

        var config = await _layoutDb.GetBlueprintConfigAsync(id);
        if (config == null || string.IsNullOrEmpty(config.ImagePath))
            return NotFound(new { error = "도면 이미지가 없습니다." });

        // ImagePath 는 서버 생성값("uploads/blueprint-bg-{id}.ext") — 사용자 입력 아님(경로 조작 불가).
        var filePath = Path.Combine(TwmsDataPath.Base, config.ImagePath);
        if (!System.IO.File.Exists(filePath))
            return NotFound(new { error = "이미지 파일을 찾을 수 없습니다." });

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        var bytes = await System.IO.File.ReadAllBytesAsync(filePath);
        return File(bytes, mime, $"{Sanitize(layout.Name)}{ext}");
    }

    /// <summary>파일명에 쓸 수 없는 문자를 '_' 로 치환 (한글 레이아웃명 다운로드 안전).</summary>
    private static string Sanitize(string? name)
    {
        var s = (name ?? "layout").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrEmpty(s) ? "layout" : s;
    }
}
