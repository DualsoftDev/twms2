using Microsoft.Extensions.Options;

namespace Twms2.Server.Services;

public class PingScheduleOptions
{
    public int IntervalMinutes { get; set; } = 5;
    public int Phase1MaxConcurrency { get; set; } = 10;
}

/// <summary>
/// 주기적 핑 테스트 백그라운드 서비스.
/// 1차: AugIp만 있는 자산 ICMP ping → 실패 게이트웨이 수집.
/// 2차: AugIpVia가 있는 자산 DeepPinger.dll 테스트 (실패 게이트웨이 스킵).
/// 사이클 완료 후 DB 배치 저장 + 상태 변경 이력 기록.
/// </summary>
public class PingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PingBackgroundService> _logger;
    private readonly PingScheduleOptions _options;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    public PingBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<PingScheduleOptions> options,
        ILogger<PingBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("주기적 Ping 서비스 시작 (간격: {Interval}분)", _options.IntervalMinutes);

        // 서버 기동 직후 잠시 대기 (다른 서비스 초기화 완료 대기)
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // DB에서 이전 핑 결과 캐시 복원
        await LoadPingCacheAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            // 중복 실행 방지: 이전 사이클 진행 중이면 스킵
            if (!_cycleLock.Wait(0))
            {
                _logger.LogWarning("이전 Ping 사이클 진행중, 이번 사이클 스킵");
            }
            else
            {
                try
                {
                    await RunPingCycleAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _cycleLock.Release();
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "주기적 Ping 사이클 실패");
                }
                finally
                {
                    _cycleLock.Release();
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), stoppingToken);
        }

        _logger.LogInformation("주기적 Ping 서비스 종료");
    }

    private async Task LoadPingCacheAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var pingDb = scope.ServiceProvider.GetRequiredService<PingDbService>();
            await pingDb.LoadPingCacheFromDbAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "핑 캐시 DB 복원 실패");
        }
    }

    private async Task RunPingCycleAsync(CancellationToken ct)
    {
        _logger.LogInformation("주기적 Ping 사이클 시작");

        using var scope = _scopeFactory.CreateScope();
        var pingService = scope.ServiceProvider.GetRequiredService<PingService>();
        var pingDb = scope.ServiceProvider.GetRequiredService<PingDbService>();

        // 1차: AugIp만 있는 자산 ICMP ping (병합 자산도 함께 반환)
        var (failedViaIps, mergedAssets) = await pingService.PingPhase1Async(ct, _options.Phase1MaxConcurrency);

        // 2차: AugIpVia가 있는 자산 (1차에서 로드한 자산 재활용)
        await pingService.PingPhase2Async(failedViaIps, mergedAssets, ct);

        // DB 배치 저장 + 상태 변경 이력 기록
        await pingDb.FlushPingToDbAsync();

        _logger.LogInformation("주기적 Ping 사이클 완료");
    }
}
