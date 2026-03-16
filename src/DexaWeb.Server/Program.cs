using System.Text.RegularExpressions;
using DexaWeb.Dexa;
using DexaWeb.Server.Components;
using DexaWeb.Server.Data;
using DexaWeb.Server.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.FileProviders;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

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
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DexaWeb.Server.Services.DexaAuthHandler>("DexaAuth", null);
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, AuthStateProvider>();
builder.Services.AddScoped<AuthStateProvider>();

// DEXA Client (Bridge 대체 - Akka TCP 직접 연결)
builder.Services.Configure<DexaClientOptions>(builder.Configuration.GetSection("DexaServer"));
builder.Services.AddSingleton<IDexaClient, DexaClient>();
builder.Services.AddSingleton<DexaNotificationService>();
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

// 업로드 파일 서빙 (wwwroot/uploads — 런타임에 생성되므로 MapStaticAssets 이전에 등록)
var uploadsPath = Path.Combine(app.Environment.WebRootPath, "uploads");
Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
