using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// DB 관리(DatabaseManagement.razor) 정적 페이지용 API. 관리자 전용.
/// - GET    /api/admin/database         : DB 통계 + 마이그레이션 이력 + 레이아웃 목록(배치 매핑용) 1회 조회.
/// - POST   /api/admin/database/import   : DEXA.sqlite3 업로드 → 분석(미리보기)만. 커밋하지 않음.
///                                         미리보기(신규/덮어쓰기 건수) + 라인 프리뷰 + 임시파일 토큰 반환.
/// - POST   /api/admin/database/apply-aug: 토큰으로 aug/연결/라인/그룹 실제 이전(전체 교체 커밋).
/// - POST   /api/admin/database/positions: 토큰 + 라인↔레이아웃 매핑으로 자산/그룹 배치 가져오기.
/// - POST   /api/admin/database/cancel   : 보류 중인 임시파일 정리(닫기/취소).
/// 기존 서비스(TwmDbService / LayoutDbService / DexaFileImportService)를 얇게 래핑 — 신규 비즈니스 로직 없음.
/// 모든 가져오기는 기존 데이터를 전체 교체하는 파괴적 작업이므로 컨트롤러 전체를 Admin 으로 보호한다.
/// </summary>
[ApiController]
[Route("api/admin/database")]
[Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
public class AdminDatabaseController : ControllerBase
{
    private readonly TwmDbService _twmDb;
    private readonly LayoutDbService _layoutDb;
    private readonly DexaFileImportService _import;
    private readonly ILogger<AdminDatabaseController> _logger;

    // DEXA.sqlite3 임시 저장 경로 (업로드 → 가져오기 → 배치 가져오기 2단계 사이 유지)
    private static readonly string ImportTempDir =
        Path.Combine(TwmsDataPath.Base, "import-tmp");

    private const long MaxImportSize = 500L * 1024 * 1024; // 500MB (Blazor 페이지와 동일)

    public AdminDatabaseController(
        TwmDbService twmDb,
        LayoutDbService layoutDb,
        DexaFileImportService import,
        ILogger<AdminDatabaseController> logger)
    {
        _twmDb = twmDb;
        _layoutDb = layoutDb;
        _import = import;
        _logger = logger;
    }

    // ──────────────── 조회 ────────────────

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var stats = await _twmDb.GetStatsAsync();
        var migrations = await _twmDb.GetMigrationsAsync();
        var layouts = await _layoutDb.GetAllLayoutsAsync();

        return Ok(new
        {
            stats = new
            {
                schemaVersion = stats.SchemaVersion,
                assetAugCount = stats.AssetAugCount,
                assetConnCount = stats.AssetConnCount,
                layoutLineCount = stats.LayoutLineCount,
                layoutGroupCount = stats.LayoutGroupCount,
            },
            migrations = migrations
                .Select(m => new
                {
                    version = m.Version,
                    description = m.Description,
                    appliedAt = m.AppliedAt,
                })
                .ToList(),
            layouts = layouts
                .Select(l => new { id = l.Id, name = l.Name })
                .ToList(),
        });
    }

    // ──────────────── 1단계: DEXA 파일 업로드 → 분석(미리보기, 커밋 없음) ────────────────

    [HttpPost("import")]
    [RequestSizeLimit(MaxImportSize + 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "DEXA.sqlite3 파일을 선택해주세요." });
        if (file.Length > MaxImportSize)
            return BadRequest(new { error = "파일 크기가 500MB를 초과합니다." });

        var ext = Path.GetExtension(file.FileName);
        if (!IsSqliteExt(ext))
            return BadRequest(new { error = ".sqlite3 / .sqlite / .db 파일만 업로드할 수 있습니다." });

        Directory.CreateDirectory(ImportTempDir);
        var token = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(ImportTempDir, $"dexa_import_{token}.sqlite3");

        try
        {
            await using (var fs = System.IO.File.Create(tempPath))
                await file.CopyToAsync(fs);

            // 커밋 없이 분석만 — 무엇이 업데이트될지 미리보기. 실제 반영은 apply-aug 에서.
            var preview = await _import.PreviewImportAsync(tempPath);
            var previews = await _import.PreviewLinesFromFileAsync(tempPath);
            var layouts = await _layoutDb.GetAllLayoutsAsync();

            return Ok(new
            {
                ok = true,
                token,
                preview = new
                {
                    totalRead    = preview.TotalRead,
                    augInsert    = preview.AugInsert,
                    augUpdate    = preview.AugUpdate,
                    connInsert   = preview.ConnInsert,
                    connUpdate   = preview.ConnUpdate,
                    connSkipped  = preview.ConnSkipped,
                    lineCurrent  = preview.LineCurrent,
                    lineFile     = preview.LineFile,
                    groupCurrent = preview.GroupCurrent,
                    groupFile    = preview.GroupFile,
                },
                linePreviews = previews
                    .Select(p => new { id = p.Id, name = p.Name, selfW = p.SelfW, selfH = p.SelfH })
                    .ToList(),
                layouts = layouts
                    .Select(l => new { id = l.Id, name = l.Name })
                    .ToList(),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA 분석 실패");
            TryDelete(tempPath);
            return Ok(new { ok = false, fatalError = ex.Message });
        }
    }

    // ──────────────── 2단계: aug/연결/라인/그룹 이전하기(실제 커밋) ────────────────

    public record ApplyAugDto(string? Token);

    [HttpPost("apply-aug")]
    public async Task<IActionResult> ApplyAug([FromBody] ApplyAugDto dto)
    {
        var tempPath = ResolveTokenPath(dto?.Token);
        if (tempPath == null || !System.IO.File.Exists(tempPath))
            return BadRequest(new { error = "임시 파일을 찾을 수 없습니다. 먼저 데이터를 가져오세요." });

        try
        {
            var result = await _import.ImportFromFileAsync(tempPath);

            if (result.HasFatalError)
                return Ok(new { ok = false, fatalError = result.FatalError });

            var stats = await _twmDb.GetStatsAsync();
            return Ok(new
            {
                ok = true,
                result = new
                {
                    totalRead     = result.TotalRead,
                    augImported   = result.AugImported,
                    connImported  = result.ConnImported,
                    connSkipped   = result.ConnSkipped,
                    lineImported  = result.LineImported,
                    groupImported = result.GroupImported,
                    errorCount    = result.ErrorCount,
                },
                stats = new
                {
                    schemaVersion    = stats.SchemaVersion,
                    assetAugCount    = stats.AssetAugCount,
                    assetConnCount   = stats.AssetConnCount,
                    layoutLineCount  = stats.LayoutLineCount,
                    layoutGroupCount = stats.LayoutGroupCount,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA aug 이전 실패");
            return Ok(new { ok = false, fatalError = ex.Message });
        }
        // 임시파일은 배치 단계까지 유지 — 여기서 삭제하지 않는다 (닫기/취소 시 정리).
    }

    // ──────────────── 3단계: 배치(라인→레이아웃 매핑) 가져오기 ────────────────

    public record PositionImportDto(string? Token, List<LineMapDto>? Mappings);
    public record LineMapDto(int LineId, int LayoutId);

    [HttpPost("positions")]
    public async Task<IActionResult> ImportPositions([FromBody] PositionImportDto dto)
    {
        var tempPath = ResolveTokenPath(dto?.Token);
        if (tempPath == null || !System.IO.File.Exists(tempPath))
            return BadRequest(new { error = "임시 파일을 찾을 수 없습니다. 먼저 데이터를 가져오세요." });

        var map = (dto!.Mappings ?? new List<LineMapDto>())
            .GroupBy(m => m.LineId)
            .ToDictionary(g => g.Key, g => g.Last().LayoutId);

        if (map.Count == 0)
            return BadRequest(new { error = "매핑된 라인이 없습니다." });

        try
        {
            var result = await _import.ImportPositionsFromFileAsync(tempPath, map);

            if (result.HasFatalError)
                return Ok(new { ok = false, fatalError = result.FatalError });

            return Ok(new
            {
                ok = true,
                result = new
                {
                    assetPositionsImported = result.AssetPositionsImported,
                    groupsImported = result.GroupsImported,
                    skipped = result.Skipped,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA 배치 가져오기 실패");
            return Ok(new { ok = false, fatalError = ex.Message });
        }
        // 임시파일은 닫기/취소(cancel) 시 정리 — aug 이전·배치를 임의 순서로 반복할 수 있도록 유지한다.
    }

    // ──────────────── 배치 단계 취소(임시파일 정리) ────────────────

    public record CancelDto(string? Token);

    [HttpPost("cancel")]
    public IActionResult Cancel([FromBody] CancelDto dto)
    {
        var tempPath = ResolveTokenPath(dto?.Token);
        if (tempPath != null) TryDelete(tempPath);
        return Ok(new { ok = true });
    }

    // ──────────────── 헬퍼 ────────────────

    private static bool IsSqliteExt(string? ext) =>
        string.Equals(ext, ".sqlite3", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, ".sqlite", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, ".db", StringComparison.OrdinalIgnoreCase);

    /// <summary>토큰을 임시파일 경로로 변환. 디렉터리 탈출(경로 조작) 방지.</summary>
    private static string? ResolveTokenPath(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        // 토큰은 GUID("N") 형태여야 함 — 16진수 32자리만 허용.
        if (token.Length != 32 || !token.All(Uri.IsHexDigit)) return null;
        return Path.Combine(ImportTempDir, $"dexa_import_{token}.sqlite3");
    }

    private void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "임시 파일 삭제 실패: {Path}", path);
        }
    }
}
