using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Helpers;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 레이아웃(도면 보기) 정적 페이지용 스냅샷 API. LayoutView.razor + 자식 렌더러
/// (BlueprintView / AssetPlacementView) 가 소비하는 데이터를 1회 응답으로 제공.
/// 읽기 전용 시각화 — 드래그/편집/배치는 Blazor 편집기에 유지(아래 deviation).
/// 기존 LayoutDbService / AssetStatusService 를 얇게 래핑(신규 비즈니스 로직 없음).
/// </summary>
[ApiController]
[Route("api/layout")]
// 공개 읽기(도면 보기). 쓰기 없음(편집은 Blazor /admin/layout/{id}).
public class LayoutViewController : ControllerBase
{
    private readonly LayoutDbService _layoutDb;
    private readonly AssetStatusService _status;

    public LayoutViewController(LayoutDbService layoutDb, AssetStatusService status)
    {
        _layoutDb = layoutDb;
        _status = status;
    }

    /// <summary>
    /// 레이아웃 목록 + 선택 레이아웃의 도면 설정 / 라인 영역 / 자산 위치 / 그룹 +
    /// 자산별 상태(건강/색상/온오프라인). layoutId 미지정 시 SortOrder 최상위 레이아웃.
    /// LayoutView.RefreshAsync 와 동일한 서비스 호출 구성을 그대로 이식.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? layoutId = null)
    {
        var layouts = await _layoutDb.GetAllLayoutsAsync();
        if (layouts.Count == 0)
            return Ok(new
            {
                layouts = Array.Empty<object>(),
                selectedLayoutId = 0,
                config = (object?)null,
                lines = Array.Empty<object>(),
                blueprintRects = Array.Empty<object>(),
                positions = Array.Empty<object>(),
                groups = Array.Empty<object>(),
                groupMembers = Array.Empty<object>(),
                assets = Array.Empty<object>(),
            });

        var selected = layoutId is int reqId && layouts.Any(l => l.Id == reqId)
            ? reqId
            : layouts[0].Id;

        // LayoutView.RefreshAsync 의 8개 병렬 조회를 그대로 이식.
        var linesTask   = _layoutDb.GetAllTwmsLayoutLinesAsync();
        var rectsTask   = _layoutDb.GetAllBlueprintRectsAsync(selected);
        var configTask  = _layoutDb.GetBlueprintConfigAsync(selected);
        var posTask     = _layoutDb.GetAllAssetPositionsAsync(selected);
        var statusTask  = _status.GetAssetStatusesAsync();
        var groupTask   = _layoutDb.GetAllPlacementGroupsAsync(selected);
        var memberTask  = _layoutDb.GetPlacementGroupMembersAsync(selected);
        var lgTask      = _layoutDb.GetAllTwmsLayoutGroupsAsync();
        await Task.WhenAll(linesTask, rectsTask, configTask, posTask, statusTask, groupTask, memberTask, lgTask);

        var lines        = linesTask.Result;
        var rects        = rectsTask.Result;
        var config       = configTask.Result;
        var positions    = posTask.Result;
        var statuses     = statusTask.Result;
        var groups       = groupTask.Result;
        var members      = memberTask.Result;
        var layoutGroups = lgTask.Result;

        // 자산별 층(AssetPlacementView.AssetFloorMap 이식): TwmsLayoutGroup.AssetId → Floor
        var floorMap = new Dictionary<int, int?>();
        foreach (var g in layoutGroups) floorMap[g.AssetId] = g.Floor;

        // 자산 스냅샷 (배치/그룹/라인 렌더러가 참조하는 필드만)
        var assets = statuses.Select(a => new
        {
            assetId       = a.AssetId,
            name          = a.Name,
            ip            = a.Ip,
            typeName      = a.AssetTypeName,
            typeId        = a.AssetTypeId,
            isRobotPlc    = a.AugIsRobotPLC is > 0,
            icon          = LayoutHelpers.GetAssetIcon(a.AssetTypeName, a.AugIsRobotPLC),
            health        = LayoutHelpers.GetHealthKey(a.Health),
            healthLabel   = LayoutHelpers.GetHealthLabel(a.Health),
            healthColor   = LayoutHelpers.GetHealthColor(a.Health),
            iconBgColor   = LayoutHelpers.GetIconBgColor(a.Health, a.LatestPing is { Reachable: false }),
            lineId        = a.AugLineId,
            ipVia         = a.AugIpVia,
            baseNumber    = a.AugBaseNumber,
            slotNumber    = a.AugSlotNumber,
            floor         = floorMap.GetValueOrDefault(a.AssetId),
            pingReachable = a.LatestPing?.Reachable,
            lastBackupTime = a.LastBackupTime,
        }).ToList();

        return Ok(new
        {
            layouts = layouts.Select(l => new { id = l.Id, name = l.Name, sortOrder = l.SortOrder }).ToList(),
            selectedLayoutId = selected,
            config = config == null ? null : new
            {
                imagePath   = config.ImagePath,
                imageWidth  = config.ImageWidth,
                imageHeight = config.ImageHeight,
                bgColor     = config.BgColor,
                gridColor   = config.GridColor,
                lineColor   = config.LineColor,
            },
            lines = lines.Select(l => new { id = l.Id, name = l.Name }).ToList(),
            blueprintRects = rects.Select(r => new
            {
                lineId = r.LineId, x = r.X, y = r.Y, width = r.Width, height = r.Height,
            }).ToList(),
            positions = positions.Where(p => p.Visible).Select(p => new
            {
                assetId = p.AssetId, x = p.X, y = p.Y, scale = p.Scale,
            }).ToList(),
            groups = groups.Select(g => new
            {
                id = g.Id, name = g.Name, x = g.X, y = g.Y,
                width = g.Width, height = g.Height, color = g.Color, floor = g.Floor,
            }).ToList(),
            groupMembers = members.Select(m => new { groupId = m.GroupId, assetId = m.AssetId }).ToList(),
            assets,
        });
    }

}
