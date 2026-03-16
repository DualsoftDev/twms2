using DEX.Core.Actor;
using DexaWeb.Dexa;
using Microsoft.Extensions.Caching.Memory;

namespace DexaWeb.Server.Services;

/// <summary>
/// DEXA 서버 알림을 Blazor 컴포넌트에 중계하는 서비스 (Singleton).
/// Akka.NET ServerNotifications → C# event → Blazor InvokeAsync(StateHasChanged).
/// action 테이블 변경 시 캐시 무효화 + UI 갱신 트리거.
/// </summary>
public class DexaNotificationService : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DexaNotificationService> _logger;

    /// <summary>DB 테이블 변경 알림. 인자: 테이블명</summary>
    public event Action<string>? OnDatabaseChanged;

    public DexaNotificationService(IDexaClient client, IMemoryCache cache, ILogger<DexaNotificationService> logger)
    {
        _cache = cache;
        _logger = logger;

        _subscription = client.ServerNotifications.Subscribe(notification =>
        {
            switch (notification)
            {
                case DatabaseChangedNotification dbChanged:
                    _logger.LogDebug("DB 변경 알림: {Table} {Op}", dbChanged.TableName, dbChanged.DatabaseChangeOperation);
                    InvalidateCacheFor(dbChanged.TableName);
                    OnDatabaseChanged?.Invoke(dbChanged.TableName);
                    break;

                case RefreshNotification:
                    _logger.LogDebug("전체 갱신 알림 수신");
                    InvalidateAllCache();
                    OnDatabaseChanged?.Invoke("*");
                    break;

                case ConnectivityChangedNotification conn:
                    _logger.LogInformation("DEXA 서버 연결 상태 변경: {Connected}", conn.Connected);
                    OnDatabaseChanged?.Invoke("connectivity");
                    break;
            }
        });

        _logger.LogInformation("DexaNotificationService 시작 — 서버 알림 구독 완료");
    }

    private void InvalidateCacheFor(string tableName)
    {
        switch (tableName?.ToLower())
        {
            case "action":
            case "actionlog":
                _cache.Remove("dexa_latest_actions");
                // action 캐시는 key에 limit 포함 → 대표 키만 제거
                _cache.Remove("dexa_actions_200");
                _cache.Remove("dexa_actions_100");
                break;
            case "asset":
                _cache.Remove("dexa_view_assets");
                break;
            case "agent":
                _cache.Remove("dexa_agents");
                break;
        }
    }

    private void InvalidateAllCache()
    {
        _cache.Remove("dexa_view_assets");
        _cache.Remove("dexa_agents");
        _cache.Remove("dexa_latest_actions");
        _cache.Remove("dexa_actions_200");
        _cache.Remove("dexa_actions_100");
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }
}
