using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Twms2.Server.Services;

/// <summary>
/// Blazor Server 인증 상태 — 서버 발급 HttpOnly 쿠키(twms_auth)를 단일 신원 소스로 사용.
/// GET /api/auth/me (authSession.me) 로 현재 쿠키 세션을 확인 → 정적 페이지와 동일 세션 공유.
/// (로그인/로그아웃은 /api/auth/login|logout = 정적 /login 페이지가 담당.)
/// </summary>
public class AuthStateProvider : AuthenticationStateProvider
{
    private readonly IJSRuntime _js;
    private ClaimsPrincipal _currentUser = new(new ClaimsIdentity());

    public AuthStateProvider(IJSRuntime js) => _js = js;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var me = await _js.InvokeAsync<MeResult>("authSession.me");
            _currentUser = BuildPrincipal(me);
        }
        catch
        {
            // 첫 렌더링 시 JS interop 불가 → 익명 (이후 NotifyAuthenticationStateChanged 로 재평가)
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
    }

    /// <summary>(호환용) 정적 로그인 페이지가 쿠키를 발급하므로 상태만 재평가.</summary>
    public Task LoginAsync(int userId, string userName, bool isAdmin)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
        };
        if (isAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        _currentUser = new ClaimsPrincipal(new ClaimsIdentity(claims, "TwmsApiCookie"));
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_currentUser)));
        return Task.CompletedTask;
    }

    public async Task LogoutAsync()
    {
        try { await _js.InvokeVoidAsync("authSession.logout"); } catch { }
        _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_currentUser)));
    }

    private static ClaimsPrincipal BuildPrincipal(MeResult? me)
    {
        if (me is not { Authenticated: true } || string.IsNullOrEmpty(me.Name))
            return new ClaimsPrincipal(new ClaimsIdentity());

        var claims = new List<Claim> { new(ClaimTypes.Name, me.Name) };
        if (me.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TwmsApiCookie"));
    }

    private sealed class MeResult
    {
        public bool Authenticated { get; set; }
        public string? Name { get; set; }
        public bool IsAdmin { get; set; }
    }
}
