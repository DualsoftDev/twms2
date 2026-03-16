using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace DexaWeb.Server.Services;

/// <summary>
/// Blazor Server 커스텀 인증 상태 관리.
/// ProtectedLocalStorage(탭 간 공유) + 세션 쿠키(브라우저 종료 감지).
/// </summary>
public class AuthStateProvider : AuthenticationStateProvider
{
    private readonly ProtectedLocalStorage _localStorage;
    private readonly IJSRuntime _js;
    private ClaimsPrincipal _currentUser = new(new ClaimsIdentity());

    public AuthStateProvider(ProtectedLocalStorage localStorage, IJSRuntime js)
    {
        _localStorage = localStorage;
        _js = js;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            // 세션 쿠키 확인 — 브라우저 종료 후 재접속이면 false
            var hasSession = await _js.InvokeAsync<bool>("authSession.check");
            if (!hasSession)
            {
                // 브라우저 재시작 → localStorage 인증 데이터 정리
                await _localStorage.DeleteAsync("userId");
                await _localStorage.DeleteAsync("userName");
                await _localStorage.DeleteAsync("isAdmin");
                _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
                return new AuthenticationState(_currentUser);
            }

            var userNameResult = await _localStorage.GetAsync<string>("userName");
            var isAdminResult = await _localStorage.GetAsync<bool>("isAdmin");
            var userIdResult = await _localStorage.GetAsync<int>("userId");

            if (userNameResult.Success && !string.IsNullOrEmpty(userNameResult.Value))
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Name, userNameResult.Value),
                    new(ClaimTypes.NameIdentifier, userIdResult.Value.ToString()),
                };

                if (isAdminResult.Success && isAdminResult.Value)
                {
                    claims.Add(new Claim(ClaimTypes.Role, "Admin"));
                }

                var identity = new ClaimsIdentity(claims, "DexaAuth");
                _currentUser = new ClaimsPrincipal(identity);
            }
        }
        catch
        {
            // 첫 렌더링 시 JS interop 불가 - 익명 사용자 반환
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
    }

    public async Task LoginAsync(int userId, string userName, bool isAdmin)
    {
        await _localStorage.SetAsync("userId", userId);
        await _localStorage.SetAsync("userName", userName);
        await _localStorage.SetAsync("isAdmin", isAdmin);
        await _js.InvokeVoidAsync("authSession.set");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
        };

        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }

        var identity = new ClaimsIdentity(claims, "DexaAuth");
        _currentUser = new ClaimsPrincipal(identity);

        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task LogoutAsync()
    {
        await _localStorage.DeleteAsync("userId");
        await _localStorage.DeleteAsync("userName");
        await _localStorage.DeleteAsync("isAdmin");
        await _js.InvokeVoidAsync("authSession.clear");

        _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
