using DEX.Core.Actor;
using Twms2.Dexa;
using Microsoft.Extensions.Caching.Memory;

namespace Twms2.Server.Services;

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
            case "action.base":
            case "action.schedule":
            case "actionlog":
            case "backuplog":
                _cache.Remove("dexa_latest_actions");
                _cache.Remove("dexa_actions_200");
                _cache.Remove("dexa_actions_100");
                break;
            case "asset":
                _cache.Remove("dexa_view_assets");
                break;
            case "agent":
                _cache.Remove("dexa_agents");
                break;
            case "asset.status":
            case "asset_status":
                _cache.Remove("dexa_asset_statuses");
                break;
            case "client":
                _cache.Remove("dexa_clients");
                break;
            case "dll":
                _cache.Remove("dexa_dlls");
                break;
            case "storage":
                _cache.Remove("dexa_view_assets");
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
        _cache.Remove("dexa_asset_statuses");
        _cache.Remove("dexa_clients");
        _cache.Remove("dexa_dlls");
    }

    /// <summary>설정 변경 등 수동으로 UI 갱신 트리거</summary>
    public void NotifyRefresh() => OnDatabaseChanged?.Invoke("*");

    public void Dispose()
    {
        _subscription.Dispose();
    }
}
