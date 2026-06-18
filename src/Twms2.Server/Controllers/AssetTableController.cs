using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Models.Dexa;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 자산 테이블 편집(AssetTableEditor.razor) 정적 페이지용 API.
/// GET  /api/assets/table        편집 가능한 자산 행 + 유형별 컬럼 메타데이터
/// POST /api/assets/table        인라인 편집 일괄 저장 (AssetService.SaveAssetEditRowsAsync)
/// POST /api/assets/table/batch  배치 편집(체크된 필드만 일괄 적용, AssetService.BatchUpdateAssetsAsync)
/// 기존 AssetService / LayoutDbService 를 얇게 래핑(신규 비즈니스 로직 없음).
/// </summary>
[ApiController]
[Route("api/assets/table")]
// 조회(GET)는 공개. 저장/배치(POST)는 로그인 필요(아래 액션별 [Authorize]).
public class AssetTableController : ControllerBase
{
    private readonly AssetService _assetService;
    private readonly LayoutDbService _layoutDb;

    public AssetTableController(AssetService assetService, LayoutDbService layoutDb)
    {
        _assetService = assetService;
        _layoutDb = layoutDb;
    }

    /// <summary>
    /// 편집 가능한 자산 행 + 라인 옵션 + 유형별 컬럼 가시성 메타데이터.
    /// 편집기는 로그인 필요(원본 AssetTableEditor [Authorize]). 조회도 인증 요구.
    /// </summary>
    [Authorize(AuthenticationSchemes = AuthController.Scheme)]
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var rowsTask = _assetService.GetAssetEditRowsAsync();
        var lineTask = _layoutDb.GetTwmsLayoutLineMapAsync();
        await Task.WhenAll(rowsTask, lineTask);

        var allRows = rowsTask.Result;
        var lineMap = lineTask.Result;

        // 라인명 채우기 (Razor LoadAll 이식)
        foreach (var r in allRows)
            r.LineName = r.LineId.HasValue && lineMap.TryGetValue(r.LineId.Value, out var ln) ? ln : "";

        var rows = allRows
            .OrderBy(r => r.AssetId)
            .Select(r => new
            {
                assetId = r.AssetId,
                assetTypeId = r.AssetTypeId,
                typeName = r.TypeName,
                name = r.Name,
                lineId = r.LineId,
                lineName = r.LineName,
                vendor = r.Vendor,
                spec = r.Spec,
                // DisplayIp = FTP/Drive/XP → Ip, PLC/Servo → ConnIp 자동 매핑 (AssetEditRow.DisplayIp)
                displayIp = r.DisplayIp,
                connIpVia = r.ConnIpVia,
                connBase = r.ConnBase,
                connSlot = r.ConnSlot,
                connIsRobot = r.ConnIsRobot,
                stationNumber = r.StationNumber,
                modelName = r.ModelName,
                modelVersion = r.ModelVersion,
                description = r.Description,
                agent = r.Agent,
            })
            .ToList();

        // 유형별 옵션 (전체 + 타입 탭) — Razor 의 탭 구성/AssetEditGrid 컬럼 규칙 이식
        var lineOptions = lineMap
            .OrderBy(kv => kv.Value)
            .Select(kv => new { id = kv.Key, name = kv.Value })
            .ToList();

        return Ok(new
        {
            rows,
            lineOptions,
            // 유형별 컬럼 가시성 규칙 (AssetEditGrid.HasIp/HasVia/HasVersion/HasRobot 이식)
            types = new object[]
            {
                new { id = 3, name = "FTP",       icon = "ftp",   hasVia = false, hasVersion = false, hasRobot = false },
                new { id = 4, name = "드라이브",   icon = "drive", hasVia = true,  hasVersion = true,  hasRobot = false },
                new { id = 5, name = "XP Series", icon = "hmi",   hasVia = false, hasVersion = false, hasRobot = false },
                new { id = 6, name = "XGT PLC",   icon = "plc",   hasVia = true,  hasVersion = false, hasRobot = true  },
                new { id = 7, name = "서보",       icon = "servo", hasVia = true,  hasVersion = false, hasRobot = false },
            },
            total = rows.Count,
        });
    }

    /// <summary>
    /// 인라인 편집 일괄 저장. 클라이언트가 보낸 변경 행을 서버에서 재로드한 행에 적용 후
    /// AssetService.SaveAssetEditRowsAsync 로 저장(변경 추적 스냅샷 보존을 위해 재로드 후 적용).
    /// </summary>
    [Authorize(AuthenticationSchemes = AuthController.Scheme)]
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SaveRequest request)
    {
        if (request?.Rows == null || request.Rows.Count == 0)
            return Ok(new { success = 0, fail = 0, results = Array.Empty<object>() });

        var lineMap = await _layoutDb.GetTwmsLayoutLineMapAsync();
        var allRows = await _assetService.GetAssetEditRowsAsync();
        var rowMap = allRows.ToDictionary(r => r.AssetId);

        // 클라이언트가 보낸 변경분을 재로드한 행에 적용. null(미전송) = 변경 안 함.
        // nullable 정수/LineId 는 *Set 플래그로 명시적 클리어를 구분.
        foreach (var edit in request.Rows)
        {
            if (!rowMap.TryGetValue(edit.AssetId, out var row)) continue;

            if (edit.Name != null) row.Name = edit.Name;
            if (edit.Description != null) row.Description = edit.Description;
            if (edit.Agent != null) row.Agent = edit.Agent;
            if (edit.Vendor != null) row.Vendor = edit.Vendor.Length == 0 ? null : edit.Vendor;
            if (edit.Spec != null) row.Spec = edit.Spec.Length == 0 ? null : edit.Spec;
            if (edit.ModelName != null) row.ModelName = edit.ModelName;
            if (edit.ModelVersion != null) row.ModelVersion = edit.ModelVersion;

            // DisplayIp 는 유형별로 Ip / ConnIp 에 자동 매핑 (AssetEditRow.DisplayIp setter)
            if (edit.DisplayIp != null) row.DisplayIp = edit.DisplayIp;
            if (edit.ConnIpVia != null) row.ConnIpVia = edit.ConnIpVia.Length == 0 ? null : edit.ConnIpVia;

            if (edit.ConnBase.HasValue) row.ConnBase = edit.ConnBase.Value;
            if (edit.ConnSlotSet) row.ConnSlot = edit.ConnSlot;
            if (edit.ConnIsRobotSet) row.ConnIsRobot = edit.ConnIsRobot;
            if (edit.StationNumberSet) row.StationNumber = edit.StationNumber;

            if (edit.LineIdSet)
            {
                row.LineId = edit.LineId;
                row.LineName = edit.LineId.HasValue && lineMap.TryGetValue(edit.LineId.Value, out var ln) ? ln : "";
            }
        }

        var modified = allRows.Where(r => r.IsModified).ToList();
        var results = await _assetService.SaveAssetEditRowsAsync(modified);

        var fail = results.Count(r => !r.Success);
        var success = results.Count - fail;

        return Ok(new
        {
            success,
            fail,
            results = results.Select(r => new { assetId = r.AssetId, success = r.Success, error = r.Error }).ToList(),
        });
    }

    /// <summary>
    /// 배치 편집: 선택된 자산들에 체크된 필드만 일괄 적용 (AssetBatchEditDialog 이식).
    /// AssetService.BatchUpdateAssetsAsync(int[], BatchEditSpec) 호출.
    /// </summary>
    [Authorize(AuthenticationSchemes = AuthController.Scheme)]
    [HttpPost("batch")]
    public async Task<IActionResult> Batch([FromBody] BatchRequest request)
    {
        if (request?.AssetIds == null || request.AssetIds.Length == 0)
            return BadRequest(new { error = "선택된 자산이 없습니다." });

        var spec = new BatchEditSpec
        {
            AgentPreferences = request.ApplyAgent ? request.AgentPreferences ?? "" : null,
            Name = request.ApplyName ? request.Name ?? "" : null,
            Ip = request.ApplyIp ? request.Ip ?? "" : null,
            Description = request.ApplyDescription ? request.Description ?? "" : null,
            ViaIp = request.ApplyViaIp ? request.ViaIp ?? "" : null,
            BaseNumber = request.ApplyBase ? request.BaseNumber : null,
            SlotNumber = request.ApplySlot ? request.SlotNumber : null,
        };

        if (!spec.HasChanges)
            return BadRequest(new { error = "변경할 항목을 선택하세요." });

        var results = await _assetService.BatchUpdateAssetsAsync(request.AssetIds, spec);
        var fail = results.Count(r => !r.Success);
        var success = results.Count - fail;

        return Ok(new
        {
            success,
            fail,
            results = results.Select(r => new { assetId = r.AssetId, success = r.Success, error = r.ErrorMessage }).ToList(),
        });
    }

    // ── 요청 DTO ──────────────────────────────────────────────

    public class SaveRequest
    {
        public List<EditRow> Rows { get; set; } = [];
    }

    /// <summary>
    /// 편집된 행. null(미전송) = 변경 안 함. nullable 정수 컬럼은 *Set 플래그로 명시적 null 클리어 구분.
    /// </summary>
    public class EditRow
    {
        public int AssetId { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Agent { get; set; }
        public string? Vendor { get; set; }
        public string? Spec { get; set; }
        public string? ModelName { get; set; }
        public string? ModelVersion { get; set; }
        public string? DisplayIp { get; set; }
        public string? ConnIpVia { get; set; }

        public int? ConnBase { get; set; }

        public bool ConnSlotSet { get; set; }
        public int? ConnSlot { get; set; }

        public bool ConnIsRobotSet { get; set; }
        public int? ConnIsRobot { get; set; }

        public bool StationNumberSet { get; set; }
        public int? StationNumber { get; set; }

        public bool LineIdSet { get; set; }
        public int? LineId { get; set; }
    }

    public class BatchRequest
    {
        public int[] AssetIds { get; set; } = [];

        public bool ApplyAgent { get; set; }
        public string? AgentPreferences { get; set; }
        public bool ApplyName { get; set; }
        public string? Name { get; set; }
        public bool ApplyIp { get; set; }
        public string? Ip { get; set; }
        public bool ApplyDescription { get; set; }
        public string? Description { get; set; }
        public bool ApplyViaIp { get; set; }
        public string? ViaIp { get; set; }
        public bool ApplyBase { get; set; }
        public int? BaseNumber { get; set; }
        public bool ApplySlot { get; set; }
        public int? SlotNumber { get; set; }
    }
}
