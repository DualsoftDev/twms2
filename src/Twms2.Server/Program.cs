using System.Text.RegularExpressions;
using Twms2.Dexa;
using Twms2.Server.Components;
using Twms2.Server.Data;
using Twms2.Server.Services;
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

// 인증 (Blazor Server 전용 — 더미 스킴으로 [Authorize] 미들웨어 에러 방지)
builder.Services.AddAuthentication("DexaAuth")
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Twms2.Server.Services.DexaAuthHandler>("DexaAuth", null);
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, AuthStateProvider>();
builder.Services.AddScoped<AuthStateProvider>();

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
    var dexaRead = scope.ServiceProvider.GetRequiredService<DexaReadService>();
    var assets = await dexaRead.GetViewAssetsAsync();
    var actionsTask = dexaRead.GetRecentActionsAsync(200);
    var agentsTask = dexaRead.GetAgentsAsync();
    await Task.WhenAll(actionsTask, agentsTask);
    startupLogger.LogInformation("캐시 워밍업 완료: 자산 {Count}건 로드됨", assets.Count);
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
app.MapPost("/api/download/backup/bulk", async (HttpContext ctx, DexaReadService dexaRead) =>
{
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
    }

    if (filesToPack.Count == 0)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("다운로드 가능한 백업 파일이 없습니다.");
        return;
    }

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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
