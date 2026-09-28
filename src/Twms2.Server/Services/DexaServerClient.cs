using Twms2.Dexa;

namespace Twms2.Server.Services;

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

    /// <summary>
    /// AskAsync 와 같지만 실패를 삼키지 않는다.
    /// 서버 거부 사유(DexaServerException: 라이선스 만료·자산명 중복·서버 Lock)를
    /// 사용자에게 보여줘야 하는 쓰기 작업용.
    /// </summary>
    public async Task<T?> AskOrThrowAsync<T>(object message, TimeSpan? timeout = null) where T : class
    {
        try
        {
            return await _client.AskServerAsync<T>(message, timeout);
        }
        // 거부 사유는 F# 클라이언트가 이미 로깅했으므로 중복 기록하지 않는다.
        catch (Exception ex) when (ex is not DexaServerException)
        {
            _logger.LogError(ex, "DEXA Server 요청 실패: {MessageType}", message.GetType().Name);
            throw;
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
