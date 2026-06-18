using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 정적 페이지 + API 공용 인증 브리지.
/// 로그인 시 서명된 HttpOnly 쿠키(TwmsApiCookie)를 발급 → 같은 출처의 모든 요청에 자동 동봉되어
/// 서버에서 위조 불가하게 검증된다(쓰기 API 보호의 근거). 기존 UserService(SHA256) 자격증명 그대로 사용.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    public const string Scheme = "TwmsApiCookie";

    private readonly UserService _users;

    public AuthController(UserService users) => _users = users;

    public record LoginRequest(string UserName, string Password);

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.UserName))
            return BadRequest(new { success = false, message = "사용자 ID를 입력하세요." });

        var result = await _users.LoginAsync(req.UserName, req.Password ?? "");
        if (result?.Success != true || result.User is null)
            // 사용자 열거 방지: '없는 ID' vs '틀린 비밀번호'를 구분하지 않는 단일 메시지.
            return Unauthorized(new { success = false, message = "아이디 또는 비밀번호가 올바르지 않습니다." });

        var u = result.User;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, u.Name ?? req.UserName),
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
        };
        if (u.Admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        await HttpContext.SignInAsync(Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12),
        });

        return Ok(new { success = true, user = new { id = u.Id, name = u.Name, isAdmin = u.Admin } });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(Scheme);
        return Ok(new { success = true });
    }

    /// <summary>현재 쿠키 세션의 신원. 미인증이면 authenticated:false (200).</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var auth = await HttpContext.AuthenticateAsync(Scheme);
        if (!auth.Succeeded || auth.Principal?.Identity?.IsAuthenticated != true)
            return Ok(new { authenticated = false });

        var p = auth.Principal;
        return Ok(new
        {
            authenticated = true,
            name = p.Identity!.Name,
            isAdmin = p.IsInRole("Admin"),
        });
    }
}
