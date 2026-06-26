using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Helpers;
using Twms2.Server.Models.Twm;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 정적 레이아웃 편집기(/admin/layout/{id}/edit) 쓰기 API.
/// Blazor LayoutEditor / BlueprintEditor / PlacementEditor 의 저장 로직을 그대로 이식.
/// 기존 LayoutDbService 의 공개 메서드만 얇게 래핑(신규 비즈니스 로직 없음). Admin 전용.
/// </summary>
[ApiController]
[Route("api/admin/layout")]
[Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
public class LayoutEditController : ControllerBase
{
    private readonly LayoutDbService _layoutDb;
    private readonly AssetStatusService _status;
    private readonly LayoutNotificationService _notify;

    public LayoutEditController(LayoutDbService layoutDb, AssetStatusService status, LayoutNotificationService notify)
    {
        _layoutDb = layoutDb;
        _status = status;
        _notify = notify;
    }

    // ──────────────── 편집기 초기 데이터 ────────────────

    /// <summary>
    /// 편집기 3개 탭(도면 설정 / 라인 영역 / 자산 배치)이 소비하는 모든 데이터를 1회 응답으로 제공.
    /// PlacementEditor.LoadDataAsync + BlueprintEditor.LoadDataAsync 의 합집합.
    /// </summary>
    [HttpGet("{id:int}/edit-data")]
    public async Task<IActionResult> GetEditData(int id)
    {
        var layouts = await _layoutDb.GetAllLayoutsAsync();
        var layout = layouts.FirstOrDefault(l => l.Id == id);
        if (layout == null)
            return NotFound(new { error = "레이아웃을 찾을 수 없습니다." });

        var statusTask = _status.GetAssetStatusesAsync();
        var configTask = _layoutDb.GetBlueprintConfigAsync(id);
        var rectsTask  = _layoutDb.GetAllBlueprintRectsAsync(id);
        var linesTask  = _layoutDb.GetAllTwmsLayoutLinesAsync();
        var posTask    = _layoutDb.GetAllAssetPositionsAsync(id);
        var groupTask  = _layoutDb.GetAllPlacementGroupsAsync(id);
        var memberTask = _layoutDb.GetPlacementGroupMembersAsync(id);
        var otherTask  = _layoutDb.GetAssetIdsPlacedOnOtherLayoutsAsync(id);
        await Task.WhenAll(statusTask, configTask, rectsTask, linesTask, posTask, groupTask, memberTask, otherTask);

        var config = configTask.Result;

        return Ok(new
        {
            layout = new { id = layout.Id, name = layout.Name },
            config = config == null ? null : new
            {
                imagePath   = config.ImagePath,
                imageWidth  = config.ImageWidth,
                imageHeight = config.ImageHeight,
                bgColor     = config.BgColor,
                gridColor   = config.GridColor,
                gridEnabled = config.GridEnabled,
                gridSize    = config.GridSize,
            },
            lines = linesTask.Result.Select(l => new { id = l.Id, name = l.Name }).ToList(),
            rects = rectsTask.Result.Select(r => new
            {
                lineId = r.LineId, x = r.X, y = r.Y, width = r.Width, height = r.Height,
            }).ToList(),
            positions = posTask.Result.Select(p => new
            {
                assetId = p.AssetId, x = p.X, y = p.Y, scale = p.Scale, visible = p.Visible,
            }).ToList(),
            groups = groupTask.Result.Select(g => new
            {
                id = g.Id, name = g.Name, x = g.X, y = g.Y,
                width = g.Width, height = g.Height, color = g.Color, floor = g.Floor,
            }).ToList(),
            groupMembers = memberTask.Result.Select(m => new { groupId = m.GroupId, assetId = m.AssetId }).ToList(),
            placedOnOtherLayouts = otherTask.Result.ToList(),
            assets = statusTask.Result.Select(a => new
            {
                assetId    = a.AssetId,
                name       = a.Name,
                ip         = a.Ip,
                typeName   = a.AssetTypeName,
                icon       = LayoutHelpers.GetAssetIcon(a.AssetTypeName, a.AugIsRobotPLC),
                isRobotPlc = a.AugIsRobotPLC is > 0,
                lineId     = a.AugLineId,
                lineName   = a.LayoutLineName,
            }).ToList(),
        });
    }

    // ──────────────── 도면 설정 (배경색 / 그리드) ────────────────

    public record ConfigDto(string? BgColor, string? GridColor, bool GridEnabled, int GridSize);

    [HttpPut("{id:int}/config")]
    public async Task<IActionResult> SaveConfig(int id, [FromBody] ConfigDto dto)
    {
        var config = await _layoutDb.GetBlueprintConfigAsync(id) ?? new TwmsBlueprintConfig();
        config.LayoutId = id;
        config.BgColor = string.IsNullOrWhiteSpace(dto.BgColor) ? "#1a1a2e" : dto.BgColor;
        config.GridColor = string.IsNullOrWhiteSpace(dto.GridColor) ? "#e0e0e0" : dto.GridColor;
        config.GridEnabled = dto.GridEnabled;
        config.GridSize = dto.GridSize is >= 5 and <= 100 ? dto.GridSize : 20;
        await _layoutDb.UpsertBlueprintConfigAsync(config);
        return Ok(new { ok = true });
    }

    // ──────────────── 도면 이미지 업로드 / 삭제 ────────────────

    [HttpPost("{id:int}/image")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(int id, IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "이미지 파일이 없습니다." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".svg"))
            return BadRequest(new { error = "PNG, JPG, SVG, WEBP 파일만 지원합니다." });

        var uploadsDir = TwmsDataPath.Uploads;
        Directory.CreateDirectory(uploadsDir);
        foreach (var f in Directory.GetFiles(uploadsDir, $"blueprint-bg-{id}.*"))
            System.IO.File.Delete(f);

        var filePath = Path.Combine(uploadsDir, $"blueprint-bg-{id}{ext}");
        await using (var stream = System.IO.File.Create(filePath))
            await file.CopyToAsync(stream);

        var (imgW, imgH) = ReadImageSize(filePath);

        var config = await _layoutDb.GetBlueprintConfigAsync(id) ?? new TwmsBlueprintConfig();
        config.LayoutId = id;
        config.ImagePath = $"uploads/blueprint-bg-{id}{ext}";
        config.ImageWidth = imgW > 0 ? imgW : null;
        config.ImageHeight = imgH > 0 ? imgH : null;
        await _layoutDb.UpsertBlueprintConfigAsync(config);

        return Ok(new { ok = true, imagePath = config.ImagePath, imageWidth = config.ImageWidth, imageHeight = config.ImageHeight });
    }

    [HttpDelete("{id:int}/image")]
    public async Task<IActionResult> RemoveImage(int id)
    {
        var config = await _layoutDb.GetBlueprintConfigAsync(id);
        if (config != null && !string.IsNullOrEmpty(config.ImagePath))
        {
            var fullPath = Path.Combine(TwmsDataPath.Base, config.ImagePath);
            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
        }
        config ??= new TwmsBlueprintConfig();
        config.LayoutId = id;
        config.ImagePath = null;
        config.ImageWidth = null;
        config.ImageHeight = null;
        await _layoutDb.UpsertBlueprintConfigAsync(config);
        return Ok(new { ok = true });
    }

    // ──────────────── 라인 영역(BlueprintRect) 저장 ────────────────

    public record RectDto(int LineId, double X, double Y, double Width, double Height);
    public record RectsSaveDto(List<RectDto>? Rects);

    [HttpPut("{id:int}/rects")]
    public async Task<IActionResult> SaveRects(int id, [FromBody] RectsSaveDto dto)
    {
        var rects = dto?.Rects ?? [];
        var keep = rects.Select(r => r.LineId).ToHashSet();

        // 화면에서 사라진 라인 영역 삭제
        var existing = await _layoutDb.GetAllBlueprintRectsAsync(id);
        foreach (var r in existing.Where(r => !keep.Contains(r.LineId)))
            await _layoutDb.DeleteBlueprintRectAsync(id, r.LineId);

        // 나머지 upsert
        foreach (var r in rects)
            await _layoutDb.UpsertBlueprintRectAsync(new TwmsBlueprintRect
            {
                LayoutId = id, LineId = r.LineId,
                X = Math.Round(r.X, 2), Y = Math.Round(r.Y, 2),
                Width = Math.Round(Math.Max(30, r.Width), 2), Height = Math.Round(Math.Max(20, r.Height), 2),
            });

        return Ok(new { ok = true, count = rects.Count });
    }

    // ──────────────── 자산 배치(Position + Group) 저장 ────────────────

    public record PositionDto(int AssetId, double X, double Y, double Scale, bool Visible);
    public record GroupDto(string? Name, double X, double Y, double Width, double Height, string? Color, int Floor, List<int>? MemberIds);
    public record PlacementSaveDto(List<PositionDto>? Positions, List<GroupDto>? Groups);

    [HttpPut("{id:int}/placement")]
    public async Task<IActionResult> SavePlacement(int id, [FromBody] PlacementSaveDto dto)
    {
        var positions = dto?.Positions ?? [];
        var groups = dto?.Groups ?? [];

        // PlacementEditor.ApplyChangesAsync 이식: 위치 batch upsert + 그룹 전체 교체.
        if (positions.Count > 0)
            await _layoutDb.UpsertAssetPositionBatchAsync(positions.Select(p => new TwmsAssetPosition
            {
                LayoutId = id, AssetId = p.AssetId,
                X = Math.Round(p.X, 2), Y = Math.Round(p.Y, 2),
                Scale = p.Scale > 0 ? p.Scale : 1.0, Visible = p.Visible,
            }).ToList());

        await _layoutDb.DeleteAllPlacementGroupsAsync(id);
        foreach (var g in groups)
        {
            var newId = await _layoutDb.InsertPlacementGroupAsync(new TwmsPlacementGroup
            {
                LayoutId = id, Name = g.Name ?? "그룹",
                X = Math.Round(g.X, 2), Y = Math.Round(g.Y, 2),
                Width = Math.Round(g.Width, 2), Height = Math.Round(g.Height, 2),
                Color = g.Color, Floor = g.Floor == 0 ? 1 : g.Floor,
            });
            var memberIds = (g.MemberIds ?? []).Distinct().ToList();
            if (memberIds.Count > 0)
                await _layoutDb.SetPlacementGroupMembersAsync(newId, memberIds);
        }

        _notify.NotifyLayoutChanged(id);
        return Ok(new { ok = true, positions = positions.Count(p => p.Visible), groups = groups.Count });
    }

    // ──────────────── 이미지 크기 판독 (LayoutEditor.ReadImageSize 이식) ────────────────

    private static (double w, double h) ReadImageSize(string filePath)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(filePath);
            if (bytes.Length > 24 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (w, h);
            }
            if (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                int i = 2;
                while (i + 8 < bytes.Length)
                {
                    if (bytes[i] != 0xFF) break;
                    byte marker = bytes[i + 1];
                    if (marker == 0xC0 || marker == 0xC2)
                    {
                        int h = (bytes[i + 5] << 8) | bytes[i + 6];
                        int w = (bytes[i + 7] << 8) | bytes[i + 8];
                        return (w, h);
                    }
                    int segLen = (bytes[i + 2] << 8) | bytes[i + 3];
                    i += 2 + segLen;
                }
            }
        }
        catch { /* ignore */ }
        return (0, 0);
    }
}
