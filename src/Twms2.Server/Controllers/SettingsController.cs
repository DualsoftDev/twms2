using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Models.Twm;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 설정(Settings.razor + 자식 탭) 정적 페이지용 API.
/// - GET  /api/settings        : 일반(App 설정) + 라인 목록 + 매뉴얼 목록 1회 조회.
/// - POST /api/settings/brand  : 사이드바 제목/부제 저장 (App:NavTitle/NavSubtitle).
/// - POST /api/settings/lines  : 라인 추가/수정 (Upsert).
/// - DELETE /api/settings/lines/{id} : 라인 삭제 (배정 자산 있으면 거부).
/// - POST /api/settings/manuals: 매뉴얼(키워드+PDF) 업로드.
/// - DELETE /api/settings/manuals/{id} : 매뉴얼 삭제 (파일 포함).
/// - POST /api/settings/logo   : 사이드바 로고 업로드 (app-logo.* 저장). PNG/JPG→SVG 변환은 클라이언트(logo-converter.js)에서 수행 후 .svg 로 업로드.
/// - DELETE /api/settings/logo : 사이드바 로고 삭제 (app-logo.* 제거).
/// 기존 서비스(AppSettingsEditor / LayoutDbService / ManualDbService)를 얇게 래핑(신규 비즈니스 로직 없음).
/// </summary>
[ApiController]
[Route("api/settings")]
// 설정은 관리(admin) 페이지 — 조회/쓰기 모두 Admin 로그인 필요(페이지 게이트도 /settings 를 Admin 로 보호).
[Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
public class SettingsController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly AppSettingsEditor _settings;
    private readonly LayoutDbService _layout;
    private readonly ManualDbService _manual;

    public SettingsController(IConfiguration config, AppSettingsEditor settings, LayoutDbService layout, ManualDbService manual)
    {
        _config = config;
        _settings = settings;
        _layout = layout;
        _manual = manual;
    }

    // ──────────────── 조회 ────────────────

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var linesTask = _layout.GetAllTwmsLayoutLinesAsync();
        var manualsTask = _manual.GetAllManualsAsync();
        await Task.WhenAll(linesTask, manualsTask);

        var lines = linesTask.Result
            .Select(l => new
            {
                id = l.Id,
                name = l.Name,
                updatedAt = l.UpdatedAt,
            })
            .ToList();

        var manuals = manualsTask.Result
            .Select(m => new
            {
                id = m.Id,
                keyword = m.Keyword,
                fileName = m.FileName,
                storedFileName = m.StoredFileName,
                uploadedAt = m.UploadedAt,
            })
            .ToList();

        // 브랜드는 캐시에서(저장 즉시 반영). 그 외 App 설정은 IConfiguration 경유.
        var brand = _settings.GetBrand();

        return Ok(new
        {
            general = new
            {
                appTitle = _config["App:Title"] ?? "TWM",
                showDate = _config.GetValue<bool>("App:ShowDate"),
                logoUrl = ScanLogoUrl(),
                // 사이드바 브랜드 — 로고 마크 우측의 제목/부제(저장 즉시 반영). 미설정 시 기본값.
                navTitle = brand.Title,
                navSubtitle = brand.Subtitle,
            },
            lines,
            manuals,
        });
    }

    // ──────────────── 사이드바 브랜드(제목/부제) 저장 ────────────────

    public record BrandDto(string? NavTitle, string? NavSubtitle);

    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    [HttpPost("brand")]
    public async Task<IActionResult> SaveBrand([FromBody] BrandDto dto)
    {
        var title = (dto.NavTitle ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "제목을 입력해주세요." });

        // 부제는 비워둘 수 있음(숨김). 과도한 길이는 잘라 저장.
        var subtitle = (dto.NavSubtitle ?? "").Trim();
        if (title.Length > 40) title = title[..40];
        if (subtitle.Length > 80) subtitle = subtitle[..80];

        await _settings.SaveBrandAsync(title, subtitle);
        return Ok(new { ok = true, navTitle = title, navSubtitle = subtitle });
    }

    // ──────────────── 라인 관리 ────────────────

    public record LineDto(int Id, string? Name);

    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    [HttpPost("lines")]
    public async Task<IActionResult> SaveLine([FromBody] LineDto dto)
    {
        var name = (dto.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "라인 이름을 입력해주세요." });

        var id = dto.Id;
        if (id <= 0)
        {
            // 신규: 다음 ID 계산 (SettingsLines.AddLineAsync 와 동일)
            var existing = await _layout.GetAllTwmsLayoutLinesAsync();
            id = existing.Count > 0 ? existing.Max(l => l.Id) + 1 : 1;
        }

        await _layout.UpsertTwmsLayoutLineAsync(new TwmsLayoutLine { Id = id, Name = name });
        return Ok(new { ok = true, id, name });
    }

    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    [HttpDelete("lines/{id:int}")]
    public async Task<IActionResult> DeleteLine(int id)
    {
        var assetCount = await _layout.CountAssetsByLineIdAsync(id);
        if (assetCount > 0)
            return Conflict(new { error = $"이 라인에 {assetCount}개의 자산이 배정되어 있어 삭제할 수 없습니다.", assetCount });

        await _layout.DeleteTwmsLayoutLineAsync(id);
        return Ok(new { ok = true });
    }

    // ──────────────── 매뉴얼 관리 ────────────────

    private const long MaxManualSize = 50L * 1024 * 1024; // 50MB

    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    [HttpPost("manuals")]
    [RequestSizeLimit(MaxManualSize + 1024 * 1024)]
    public async Task<IActionResult> UploadManual([FromForm] string keyword, IFormFile? file)
    {
        keyword = (keyword ?? "").Trim();
        if (string.IsNullOrWhiteSpace(keyword))
            return BadRequest(new { error = "키워드를 입력해주세요." });
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "PDF 파일을 선택해주세요." });
        if (file.Length > MaxManualSize)
            return BadRequest(new { error = "파일 크기가 50MB를 초과합니다." });
        if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "PDF 파일만 업로드할 수 있습니다." });

        Directory.CreateDirectory(TwmsDataPath.Manuals);
        var storedFileName = $"{Guid.NewGuid():N}.pdf";
        var filePath = Path.Combine(TwmsDataPath.Manuals, storedFileName);

        await using (var fs = System.IO.File.Create(filePath))
            await file.CopyToAsync(fs);

        var newId = await _manual.InsertManualAsync(new TwmsManual
        {
            Keyword = keyword,
            FileName = file.FileName,
            StoredFileName = storedFileName,
        });

        return Ok(new { ok = true, id = newId });
    }

    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    [HttpDelete("manuals/{id:int}")]
    public async Task<IActionResult> DeleteManual(int id)
    {
        var manual = await _manual.GetManualByIdAsync(id);
        if (manual == null)
            return NotFound(new { error = "매뉴얼을 찾을 수 없습니다." });

        var filePath = Path.Combine(TwmsDataPath.Manuals, manual.StoredFileName);
        if (System.IO.File.Exists(filePath))
        {
            try { System.IO.File.Delete(filePath); }
            catch { /* 파일 삭제 실패는 무시 (SettingsManuals 와 동일) */ }
        }

        await _manual.DeleteManualAsync(id);
        return Ok(new { ok = true });
    }

    // ──────────────── 사이드바 로고 ────────────────

    private static readonly string[] AllowedLogoExt = { ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp" };
    private const long MaxLogoSize = 2L * 1024 * 1024; // 2MB (SettingsGeneral.UploadLogoAsync 와 동일)

    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    [HttpPost("logo")]
    [RequestSizeLimit(MaxLogoSize + 512 * 1024)]
    public async Task<IActionResult> UploadLogo(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "이미지 파일을 선택해주세요." });
        if (file.Length > MaxLogoSize)
            return BadRequest(new { error = "파일 크기가 2MB를 초과합니다." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (Array.IndexOf(AllowedLogoExt, ext) < 0)
            return BadRequest(new { error = "PNG, JPG, SVG, GIF, WEBP 파일만 지원합니다." });

        // 기존 로고(app-logo.*) 제거 후 신규 저장 (SettingsGeneral 와 동일한 단일 파일 정책)
        Directory.CreateDirectory(TwmsDataPath.Uploads);
        foreach (var f in Directory.GetFiles(TwmsDataPath.Uploads, "app-logo.*"))
        {
            try { System.IO.File.Delete(f); } catch { /* 교체 중 잠김 무시 */ }
        }

        var filePath = Path.Combine(TwmsDataPath.Uploads, $"app-logo{ext}");
        await using (var fs = System.IO.File.Create(filePath))
            await file.CopyToAsync(fs);

        return Ok(new { ok = true, logoUrl = ScanLogoUrl() });
    }

    [Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
    [HttpDelete("logo")]
    public IActionResult DeleteLogo()
    {
        try
        {
            if (Directory.Exists(TwmsDataPath.Uploads))
                foreach (var f in Directory.GetFiles(TwmsDataPath.Uploads, "app-logo.*"))
                    System.IO.File.Delete(f);
        }
        catch { /* 삭제 실패는 무시 (SettingsGeneral.DeleteLogoAsync 와 동일) */ }
        return Ok(new { ok = true });
    }

    /// <summary>업로드 폴더의 app-logo.* 미리보기 URL (NavController.ScanLogoUrl 과 동일).</summary>
    private static string? ScanLogoUrl()
    {
        try
        {
            if (!Directory.Exists(TwmsDataPath.Uploads)) return null;
            var files = Directory.GetFiles(TwmsDataPath.Uploads, "app-logo.*");
            if (files.Length == 0) return null;
            var fi = new FileInfo(files[0]);
            return $"/uploads/{Path.GetFileName(files[0])}?t={fi.LastWriteTimeUtc.Ticks}";
        }
        catch { return null; }
    }
}
