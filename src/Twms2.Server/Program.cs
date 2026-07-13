using System.Text.RegularExpressions;
using Twms2.Dexa;
using Twms2.Server.Components;
using Twms2.Server.Data;
using Twms2.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.FileProviders;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// 런타임 데이터 디렉토리 생성 (C:\ProgramData\DualSoft\TWMS2\)
TwmsDataPath.EnsureDirectories();

// 런타임 로컬 설정 파일 (ProgramData — appsettings.json 위의 오버라이드 레이어)
if (builder.Environment.IsDevelopment())
{
    // Development: ProgramData 설정 로드하되 Kestrel 섹션 제외 → launchSettings 포트 사용
    var localConfig = new ConfigurationBuilder()
        .AddJsonFile(TwmsDataPath.LocalConfig, optional: true)
        .Build();
    builder.Configuration.AddInMemoryCollection(
        localConfig.AsEnumerable()
            .Where(kv => !kv.Key.StartsWith("Kestrel", StringComparison.OrdinalIgnoreCase))
            .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
}
else
{
    builder.Configuration.AddJsonFile(TwmsDataPath.LocalConfig, optional: true, reloadOnChange: true);
    // 프로덕션 기본 바인딩 (Kestrel:Endpoints 미설정 시)
    if (!builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
        builder.WebHost.UseUrls("http://0.0.0.0:80");
}

// Windows 서비스로 실행 가능하게 설정 (콘솔 실행도 동일하게 동작)
builder.Host.UseWindowsService();

// 인메모리 캐시 (DEXA DB 중복 조회 방지)
builder.Services.AddMemoryCache();

// MudBlazor
builder.Services.AddMudServices();

// Razor + Interactive Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(opts =>
    {
        // 로고 이미지 base64 전송 및 SVG 반환을 위한 크기 허용 (기본 32KB → 16MB)
        opts.MaximumReceiveMessageSize = 16 * 1024 * 1024;
    });

// 정적 페이지(wwwroot/app/*) 데이터 API — 격리형 호스팅(/api/*). 기존 서비스를 얇게 래핑(신규 로직 없음).
builder.Services.AddControllers();

// 인증 (Blazor Server 전용 — 더미 스킴으로 [Authorize] 미들웨어 에러 방지)
// + 정적 페이지/API 공용 쿠키 스킴(TwmsApiCookie): 서명된 HttpOnly 쿠키로 서버측 신원 검증 → 쓰기 API 보호.
builder.Services.AddAuthentication("DexaAuth")
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Twms2.Server.Services.DexaAuthHandler>("DexaAuth", null)
    .AddCookie(Twms2.Server.Controllers.AuthController.Scheme, options =>
    {
        options.Cookie.Name = "twms_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        // API/정적 페이지는 로그인 페이지 리다이렉트 대신 상태코드로 응답
        options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, AuthStateProvider>();
builder.Services.AddScoped<AuthStateProvider>();

// 인증 쿠키(twms_auth) 서명/암호화 키를 디스크에 영속 + 앱 이름 고정.
// 미설정 시(특히 Windows 서비스 실행) 재시작마다 키링이 재생성 → 기존 쿠키 무효화 → 로그인 풀림/401.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(TwmsDataPath.Base, "dp-keys")))
    .SetApplicationName("Twms2");

// DEXA Client (CommProxy in-process — DexaBridge 프로세스 불필요)
builder.Services.Configure<DexaClientOptions>(builder.Configuration.GetSection("DexaServer"));
builder.Services.AddSingleton<IDexaClient, DexaDirectClient>();
builder.Services.AddSingleton<DexaNotificationService>();
builder.Services.AddSingleton<LayoutNotificationService>();
builder.Services.AddScoped<DexaServerClient>();

// DB 연결 팩토리
builder.Services.AddSingleton<DexaDbConnection>();
builder.Services.AddSingleton<TwmDbConnection>();

// TWM DB 초기화
builder.Services.AddSingleton<TwmDbInitializer>();

// Application Services
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<AssetService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<DexaReadService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<TwmDbService>();
builder.Services.AddScoped<LayoutDbService>();
builder.Services.AddScoped<PingDbService>();
builder.Services.AddScoped<ManualDbService>();
builder.Services.AddScoped<AssetStatusService>();
builder.Services.AddScoped<PingService>();
builder.Services.AddSingleton<AppSettingsEditor>();
builder.Services.AddScoped<DexaFileImportService>();

// 주기적 Ping 백그라운드 서비스
builder.Services.Configure<PingScheduleOptions>(builder.Configuration.GetSection("PingSchedule"));
builder.Services.AddHostedService<PingBackgroundService>();

// mDNS 브로드캐스트 (twms.local)
builder.Services.Configure<MdnsOptions>(builder.Configuration.GetSection("Mdns"));
builder.Services.AddHostedService<MdnsHostedService>();

var app = builder.Build();

// TWM DB 테이블 자동 생성
var twmInitializer = app.Services.GetRequiredService<TwmDbInitializer>();
await twmInitializer.InitializeAsync();

// DEXA Client 초기화 (Akka ActorSystem 시작 및 서버 연결)
var dexaClient = app.Services.GetRequiredService<IDexaClient>();
await dexaClient.InitializeAsync();

// DEXA 알림 서비스 시작 (서버 알림 → Blazor UI 실시간 갱신)
app.Services.GetRequiredService<DexaNotificationService>();

// 캐시 워밍업 (DEXA DB 미리 로드 — 완료 전까지 app.Run() 미호출 → 클라이언트 접속 불가)
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    startupLogger.LogInformation("캐시 워밍업 시작...");
    using var scope = app.Services.CreateScope();
    var sp = scope.ServiceProvider;
    var statusService = sp.GetRequiredService<AssetStatusService>();
    var dashboardService = sp.GetRequiredService<DashboardService>();
    // 첫 화면(/api/nav + /api/dashboard) 이 실제로 쓰는 합성 데이터를 통째로 데운다:
    //  - 합성 자산상태: 병합자산·에이전트·최신액션·전체액션(연속실패 집계)·핑·라인맵 + 합성 캐시 시드
    //  - 대시보드 코어: 7일 액션·드라이브 용량 등
    // 이전 워밍업은 자산·최근200건만 데워 전체액션/핑/머지자산 비용을 첫 클릭이 다 냈음(콜드).
    var statusTask = statusService.GetAssetStatusesAsync();
    var coreTask = dashboardService.GetDashboardCoreAsync();
    await Task.WhenAll(statusTask, coreTask);
    startupLogger.LogInformation("캐시 워밍업 완료: 자산 상태 {Count}건 로드됨", statusTask.Result.Count);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// HTTPS 엔드포인트가 설정된 경우 HTTP → HTTPS 자동 리다이렉트
if (app.Configuration.GetSection("Kestrel:Endpoints:Https").Exists())
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

// ── 민감 산출물 보호 게이트 ──
// 백업 ZIP(/api/download/backup/*)·DEXA 리포트 HTML(/report/*) 은 장비 프로그램/설정 덤프라
// 익명 접근 금지(로그인 필요). HTML 내비게이션은 /login 으로, 그 외(다운로드/리소스)는 401.
// UseAuthentication 보다 앞서 등록되므로 쿠키 스킴을 직접 인증한다.
app.Use(async (context, next) =>
{
    var p = context.Request.Path.Value ?? "";
    if (p.StartsWith("/report", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/api/download/backup", StringComparison.OrdinalIgnoreCase))
    {
        var r = await context.AuthenticateAsync(Twms2.Server.Controllers.AuthController.Scheme);
        if (!r.Succeeded || r.Principal?.Identity?.IsAuthenticated != true)
        {
            if (HttpMethods.IsGet(context.Request.Method)
                && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
                context.Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(p + context.Request.QueryString)}");
            else
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next();
});

// DEXA Report 정적 파일 서빙 (C:\ProgramData\LS\DEXA\Storage\Report → /report)
// 파일명에 # 문자가 포함된 경우 처리: HTML 내 href/src의 #을 %23으로 치환
var reportPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "LS", "DEXA", "Storage", "Report");
if (Directory.Exists(reportPath))
{
    // HTML 파일 요청 시 # → %23 리라이트 미들웨어 (UseStaticFiles 보다 먼저 등록)
    app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/report", out var remaining))
        {
            await next();
            return;
        }

        var subPath = remaining.Value?.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar) ?? "";
        var filePath = Path.GetFullPath(Path.Combine(reportPath, subPath));

        // 보안: reportPath 외부 접근 방지
        if (!filePath.StartsWith(reportPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 403;
            return;
        }

        // HTML 파일인 경우만 리라이트 처리
        if (File.Exists(filePath) && filePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            var html = await File.ReadAllTextAsync(filePath);
            // href, src 속성 값 내 URL 특수문자 인코딩 (파일명에 포함된 경우 브라우저 호환)
            // %를 먼저 치환해야 이후 치환된 %가 이중 인코딩되지 않음
            string EncodeUrlChars(string val) => val
                .Replace("%", "%25")
                .Replace("#", "%23")
                .Replace("?", "%3F")
                .Replace("&", "%26")
                .Replace("+", "%2B");

            html = Regex.Replace(html, @"((?:href|src)\s*=\s*"")([^""]*)("")",
                m => m.Groups[1].Value + EncodeUrlChars(m.Groups[2].Value) + m.Groups[3].Value);
            html = Regex.Replace(html, @"((?:href|src)\s*=\s*')([^']*)(')",
                m => m.Groups[1].Value + EncodeUrlChars(m.Groups[2].Value) + m.Groups[3].Value);

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(html);
            return;
        }

        await next();
    });

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(reportPath),
        RequestPath = "/report"
    });
}

// 백업 ZIP 다운로드 API (Storage\Backup\{assetId}\{version}\*.zip)
var backupBasePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "LS", "DEXA", "Storage", "Backup");
app.MapGet("/api/download/backup/{assetId:int}/{version:int}", (int assetId, int version) =>
{
    var versionDir = Path.Combine(backupBasePath, assetId.ToString(), version.ToString());
    if (!Directory.Exists(versionDir))
        return Results.NotFound("백업 파일을 찾을 수 없습니다.");

    var zipFile = Directory.GetFiles(versionDir, "*.zip").FirstOrDefault();
    if (zipFile == null)
        return Results.NotFound("백업 ZIP 파일이 없습니다.");

    // 보안: backupBasePath 외부 접근 방지
    var fullPath = Path.GetFullPath(zipFile);
    if (!fullPath.StartsWith(backupBasePath, StringComparison.OrdinalIgnoreCase))
        return Results.Forbid();

    var originalName = Path.GetFileNameWithoutExtension(zipFile);
    var fileName = $"{originalName}_V{version}.zip";
    return Results.File(fullPath, "application/zip", fileName);
});

// 프로젝트 일괄 다운로드 API (필터된 자산들의 최신 백업을 하나의 ZIP으로)
// POST: 자산 1000개 이상 시 URL 길이 제한 회피 + Response.Body 직접 스트리밍으로 메모리 절약
app.MapPost("/api/download/backup/bulk", async (HttpContext ctx, DexaReadService dexaRead, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("BulkDownload");
    int[] assetIds;
    try
    {
        assetIds = await ctx.Request.ReadFromJsonAsync<int[]>() ?? [];
    }
    catch
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("잘못된 요청 형식입니다.");
        return;
    }

    if (assetIds.Length == 0)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("다운로드할 자산이 없습니다.");
        return;
    }

    // 자산별 최신 액션(버전) 조회
    var latestActions = await dexaRead.GetLatestActionPerAssetAsync();
    var versionMap = latestActions
        .Where(a => a.Version.HasValue && a.Version > 0)
        .ToDictionary(a => a.AssetId, a => a.Version!.Value);

    // 자산명 조회 (zip 내 파일명용)
    var assets = await dexaRead.GetViewAssetsAsync();
    var nameMap = assets.ToDictionary(a => a.AssetId, a => a.DisplayName);

    // 대상 자산의 백업 파일 수집
    var filesToPack = new List<(string ZipEntryName, string FilePath)>();
    long totalBytes = 0;
    foreach (var assetId in assetIds)
    {
        if (!versionMap.TryGetValue(assetId, out var version)) continue;

        var versionDir = Path.Combine(backupBasePath, assetId.ToString(), version.ToString());
        if (!Directory.Exists(versionDir)) continue;

        var zipFile = Directory.GetFiles(versionDir, "*.zip").FirstOrDefault();
        if (zipFile == null) continue;

        var fullPath = Path.GetFullPath(zipFile);
        if (!fullPath.StartsWith(backupBasePath, StringComparison.OrdinalIgnoreCase)) continue;

        var assetName = nameMap.TryGetValue(assetId, out var n) ? n : assetId.ToString();
        var entryName = $"{assetName}_V{version}.zip";
        filesToPack.Add((entryName, fullPath));
        totalBytes += new FileInfo(fullPath).Length;
    }

    if (filesToPack.Count == 0)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("다운로드 가능한 백업 파일이 없습니다.");
        return;
    }

    // ZipArchive 는 동기 쓰기만 지원하므로 Response.Body 직접 스트리밍을 위해 이 요청에 한해 동기 IO 를 허용한다.
    // (기본값 AllowSynchronousIO=false 면 ZIP 마무리 단계에서 InvalidOperationException 이 나 다운로드가 깨진다.)
    var bodyControl = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
    if (bodyControl != null) bodyControl.AllowSynchronousIO = true;

    // 경고: 동기 스트리밍은 다운로드가 끝날 때까지 워커 스레드를 점유한다. 대용량/동시 요청이 겹치면
    // 스레드 풀이 압박될 수 있어, 큰 묶음은 운영에서 추적할 수 있도록 경고 로그를 남긴다.
    const long WarnBytes = 500L * 1024 * 1024; // 500MB
    const int WarnCount = 300;
    if (totalBytes >= WarnBytes || filesToPack.Count >= WarnCount)
        logger.LogWarning(
            "대용량 일괄다운로드: 자산 {Count}건 / 약 {SizeMB}MB 를 동기 스트리밍합니다. 동시 요청이 많으면 스레드 풀 점유에 주의하세요.",
            filesToPack.Count, totalBytes / (1024 * 1024));

    // Response.Body에 직접 스트리밍 (메모리에 전체 ZIP을 올리지 않음)
    var fileName = $"DEXA_Backup_{DateTime.Now:yyyyMMdd_HHmm}.zip";
    ctx.Response.ContentType = "application/zip";
    ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

    using var archive = new System.IO.Compression.ZipArchive(ctx.Response.Body, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);
    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (entryName, filePath) in filesToPack)
    {
        var finalName = entryName;
        var idx = 1;
        while (!usedNames.Add(finalName))
            finalName = $"{Path.GetFileNameWithoutExtension(entryName)}_{idx++}.zip";

        var entry = archive.CreateEntry(finalName, System.IO.Compression.CompressionLevel.NoCompression);
        using var entryStream = entry.Open();
        await using var fileStream = File.OpenRead(filePath);
        await fileStream.CopyToAsync(entryStream);
    }
});

// 업로드 파일 서빙 (ProgramData\DualSoft\TWMS2\uploads → /uploads)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(TwmsDataPath.Uploads),
    RequestPath = "/uploads"
});

// 매뉴얼 PDF 파일 서빙 (ProgramData\DualSoft\TWMS2\manuals → /manuals)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(TwmsDataPath.Manuals),
    RequestPath = "/manuals"
});

// 인증/인가 미들웨어 (쿠키 스킴 검증 + 컨트롤러 [Authorize] 적용)
app.UseAuthentication();
app.UseAuthorization();

// ── 페이지 인증 게이트 (선택적 로그인) ──
// 대시보드 등 모니터링/조회 페이지는 모두 공개 — 로그인을 강제하지 않는다(첫 화면=대시보드).
// 로그인은 헤더의 "로그인" 버튼으로 사용자가 직접 진입. /admin/* 과 /schedules 만 Admin 로그인 필요
// (미인증/비관리자가 URL로 직접 접근하면 /login 으로). 정적 자산은 Accept!=text/html 이라 통과.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var isHtmlNav = HttpMethods.IsGet(context.Request.Method)
        && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    var adminPath = path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/schedules", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/settings", StringComparison.OrdinalIgnoreCase)
        // 자산 테이블 편집기 — 편집 API 가 Admin 전용으로 승격되며 페이지도 Admin 요구.
        || path.Equals("/assets/table", StringComparison.OrdinalIgnoreCase);
    if (isHtmlNav && adminPath)
    {
        var result = await context.AuthenticateAsync(Twms2.Server.Controllers.AuthController.Scheme);
        var authed = result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true;
        if (!authed)
        {
            context.Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(path + context.Request.QueryString)}");
            return;
        }
        if (adminPath && result.Principal?.IsInRole("Admin") != true)
        {
            context.Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(path + context.Request.QueryString)}");
            return;
        }
    }
    await next();
});

// ── 격리형 호스팅: 정적 페이지 canonical 라우트 ──
// GET 요청을 wwwroot/app/*.html 로 short-circuit(Blazor 라우터보다 먼저). 미들웨어이므로
// 딕셔너리에서 줄을 지우면 즉시 Blazor 로 원복(.razor 페이지는 폴백으로 그대로 보존).
var staticRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["/"] = "dashboard.html",
    ["/overview"] = "dashboard.html",
    ["/statistics"] = "statistics.html",
    ["/login"] = "login.html",
    ["/history"] = "history.html",
    ["/admin"] = "admin.html",
    ["/settings"] = "settings.html",
    ["/status"] = "status.html",
    ["/schedules"] = "schedules.html",
    ["/assets"] = "asset-explorer.html",
    ["/assets/table"] = "asset-table.html",
    ["/admin/config"] = "admin-config.html",
    ["/admin/users"] = "admin-users.html",
    ["/admin/database"] = "admin-database.html",
    // 주의: 정확매칭이라 "/admin/layout" 만 정적, "/admin/layout/{id}"(LayoutEditor)는 Blazor 폴백 유지.
    ["/admin/layout"] = "layout-management.html",
};
// 파라미터 라우트(/assets/{int}, /qr/{int})는 정확매칭 딕셔너리로 불가 → 정규식으로 asset-detail.html 매핑.
// (정확매칭을 먼저 보므로 /assets·/assets/table 은 위 딕셔너리가 우선.)
var assetDetailRx = new System.Text.RegularExpressions.Regex(
    @"^/(?:assets|qr)/\d+/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
// 정적 레이아웃 편집기: /admin/layout/{id}/edit → layout-editor.html (Blazor /admin/layout/{id} 와 별개 경로).
var layoutEditorRx = new System.Text.RegularExpressions.Regex(
    @"^/admin/layout/\d+/edit/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
var staticAppRoot = Path.Combine(
    app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "app");
app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method))
    {
        var reqPath = context.Request.Path.Value ?? string.Empty;
        string? file = null;
        if (staticRoutes.TryGetValue(reqPath, out var mapped)) file = mapped;
        else if (layoutEditorRx.IsMatch(reqPath)) file = "layout-editor.html";
        else if (assetDetailRx.IsMatch(reqPath)) file = "asset-detail.html";

        var path = file is null ? null : Path.Combine(staticAppRoot, file);
        if (path is not null && File.Exists(path))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(path);
            return;
        }
    }
    await next();
});

// /api/* 컨트롤러 — Blazor 컴포넌트 엔드포인트보다 먼저 매핑.
app.MapControllers();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
