using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 사용자 관리(UserManagement.razor) 정적 페이지용 API.
/// - GET /api/admin/users : DEXA 사용자 목록(비밀번호 제외) + 사용자별 배정 권한(자산) 개수.
/// 기존 UserService(GetUsersAsync / GetPermissionsAsync)를 얇게 래핑(신규 로직 없음).
/// UserService 에 생성/수정/삭제 메서드가 없으므로 화면은 읽기 전용(원본 .razor 도 조회 전용).
/// 사용자 목록은 민감 정보이므로 GET 포함 관리자 전용.
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(AuthenticationSchemes = AuthController.Scheme, Roles = "Admin")]
public class AdminUsersController : ControllerBase
{
    private readonly UserService _users;

    public AdminUsersController(UserService users) => _users = users;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var usersTask = _users.GetUsersAsync();
        var permsTask = _users.GetPermissionsAsync();
        await Task.WhenAll(usersTask, permsTask);

        var perms = permsTask.Result;
        var permCounts = perms
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.Count());

        var users = usersTask.Result
            .OrderByDescending(u => u.Admin)
            .ThenBy(u => u.Name)
            .Select(u => new
            {
                id = u.Id,
                name = u.Name,
                admin = u.Admin,
                augRoles = u.AugRoles,
                permissionCount = permCounts.TryGetValue(u.Id, out var c) ? c : 0,
            })
            .ToList();

        return Ok(new
        {
            users,
            total = users.Count,
            adminCount = users.Count(u => u.admin),
        });
    }
}
