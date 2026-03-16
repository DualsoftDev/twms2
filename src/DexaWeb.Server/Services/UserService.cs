using DEX.Core.Actor;
using DexaWeb.Server.Models.Dexa;

namespace DexaWeb.Server.Services;

/// <summary>
/// 인증/사용자 관련 서비스.
/// 읽기: DEXA SQLite 직접 조회 (DexaReadService)
/// 인증: DEXA Server Akka 메시징 (비밀번호 복호화/비교는 서버가 내부 처리)
/// </summary>
public class UserService
{
    private readonly DexaReadService _dexaRead;
    private readonly DexaServerClient _dexa;
    private readonly ILogger<UserService> _logger;

    public UserService(DexaReadService dexaRead, DexaServerClient dexa, ILogger<UserService> logger)
    {
        _dexaRead = dexaRead;
        _dexa = dexa;
        _logger = logger;
    }

    /// <summary>
    /// DEXA Server 로그인 (Akka - 비밀번호 검증은 서버에서 처리)
    /// </summary>
    public async Task<LoginResult> LoginAsync(string userName, string password)
    {
        try
        {
            var reply = await _dexa.AskAsync<AmS2CReplyAuthenticateUser>(
                new AmC2SRequestAuthenticateUser(userName, password));

            if (reply?.User == null)
                return new LoginResult { Success = false, Message = reply?.Status ?? "인증 실패" };

            _logger.LogInformation("로그인 성공: {UserName} (Admin: {IsAdmin})", userName, reply.User.IsAdmin);

            return new LoginResult
            {
                Success = true,
                User = new LoginUser
                {
                    Id = reply.User.Id ?? 0,
                    Name = reply.User.UserName,
                    Admin = reply.User.IsAdmin,
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "로그인 실패: {UserName}", userName);
            return new LoginResult { Success = false, Message = $"서버 연결 오류: {ex.Message}" };
        }
    }

    /// <summary>
    /// 사용자 목록 조회 (SQLite - 비밀번호 제외)
    /// </summary>
    public async Task<List<User>> GetUsersAsync()
    {
        return await _dexaRead.GetUsersAsync();
    }

    /// <summary>
    /// 권한 목록 조회 (SQLite)
    /// </summary>
    public async Task<List<Permission>> GetPermissionsAsync()
    {
        return await _dexaRead.GetPermissionsAsync();
    }
}

public class LoginResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public LoginUser? User { get; set; }
}

public class LoginUser
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool Admin { get; set; }
}
