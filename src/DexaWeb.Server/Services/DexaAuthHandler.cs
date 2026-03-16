using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DexaWeb.Server.Services;

/// <summary>
/// Blazor Server용 더미 인증 핸들러.
/// 실제 인증은 AuthStateProvider(세션 스토리지)가 처리.
/// [Authorize] 미들웨어에서 DefaultScheme을 못 찾는 에러 방지용.
/// </summary>
public class DexaAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public DexaAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Blazor Server는 SignalR 위에서 동작하므로 HTTP 인증 불필요.
        // AuthenticationStateProvider가 실제 인증 상태를 관리.
        return Task.FromResult(AuthenticateResult.NoResult());
    }
}
