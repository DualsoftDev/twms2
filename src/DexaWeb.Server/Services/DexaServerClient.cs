using DexaWeb.Dexa;

namespace DexaWeb.Server.Services;

/// <summary>
/// DEXA Server 직접 통신 클라이언트 (BridgeApiClient 대체).
/// IDexaClient를 래핑하여 에러 핸들링 제공.
/// </summary>
public class DexaServerClient
{
    private readonly IDexaClient _client;
    private readonly ILogger<DexaServerClient> _logger;

    public DexaServerClient(IDexaClient client, ILogger<DexaServerClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public bool IsConnected => _client.IsConnected;

    public async Task<T?> AskAsync<T>(object message, TimeSpan? timeout = null) where T : class
    {
        try
        {
            return await _client.AskServerAsync<T>(message, timeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA Server 요청 실패: {MessageType}", message.GetType().Name);
            return null;
        }
    }

    public void Tell(object message)
    {
        try
        {
            _client.TellServer(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA Server Tell 실패: {MessageType}", message.GetType().Name);
        }
    }
}
