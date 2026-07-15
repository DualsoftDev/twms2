using Microsoft.Extensions.Options;

namespace Twms2.Server.Services;

public class MaintenanceOptions
{
    /// <summary>핑 상태전환 이력(TwmsPingLog) 보존 일수. 0 이하면 삭제하지 않음.</summary>
    public int PingLogRetentionDays { get; set; } = 365;

    /// <summary>DB 가져오기 임시파일(import-tmp) 보존 시간. 초과분은 정리.</summary>
    public int ImportTempMaxAgeHours { get; set; } = 24;
}

/// <summary>
/// 일일 유지보수 백그라운드 서비스 (24시간 상시 가동 대비 디스크 증가 방지).
/// - TwmsPingLog 보존기간 초과 행 삭제
/// - import-tmp 잔여 임시파일 정리 (사용자가 취소 없이 이탈한 업로드 sqlite, 최대 500MB/건)
/// 기동 직후 1회 실행 후 24시간 간격 반복.
/// </summary>
public class MaintenanceBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MaintenanceBackgroundService> _logger;
    private readonly MaintenanceOptions _options;

    private static readonly string ImportTempDir = Path.Combine(TwmsDataPath.Base, "import-tmp");

    public MaintenanceBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<MaintenanceOptions> options,
        ILogger<MaintenanceBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 기동 피크(마이그레이션·워밍업·첫 핑 사이클)와 겹치지 않게 잠시 대기
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanPingLogsAsync();
                CleanImportTemp();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "일일 유지보수 실패 — 다음 주기에 재시도");
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private async Task CleanPingLogsAsync()
    {
        if (_options.PingLogRetentionDays <= 0) return;
        using var scope = _scopeFactory.CreateScope();
        var pingDb = scope.ServiceProvider.GetRequiredService<PingDbService>();
        await pingDb.DeleteOldPingLogsAsync(_options.PingLogRetentionDays);
    }

    private void CleanImportTemp()
    {
        if (!Directory.Exists(ImportTempDir)) return;

        // 진행 중일 수 있는 최근 업로드는 남기고, 보존시간을 넘긴 잔여물만 정리
        var cutoff = DateTime.Now.AddHours(-Math.Max(1, _options.ImportTempMaxAgeHours));
        var removed = 0;
        long freedBytes = 0;
        foreach (var file in Directory.EnumerateFiles(ImportTempDir))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime >= cutoff) continue;
                freedBytes += info.Length;
                info.Delete();
                removed++;
            }
            catch (IOException)
            {
                // 사용 중인 파일(가져오기 진행 중)은 건너뜀
            }
        }

        if (removed > 0)
            _logger.LogInformation("import-tmp 정리: 잔여 임시파일 {Count}건 삭제 ({SizeMB}MB 확보)",
                removed, freedBytes / (1024 * 1024));
    }
}
