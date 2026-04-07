using System.Security.Cryptography;
using System.Text;
using Dapper;
using Twms2.Server.Data;
using Twms2.Server.Models.Dexa;

namespace Twms2.Server.Services;

/// <summary>
/// 인증/사용자 관련 서비스.
/// 읽기: DEXA SQLite 직접 조회 (DexaReadService)
/// 인증: DEXA SQLite DB 직접 인증 (SHA256 해시 비교, salt 없음)
/// </summary>
public class UserService
{
    private readonly DexaReadService _dexaRead;
    private readonly DexaDbConnection _dexaDb;
    private readonly ILogger<UserService> _logger;

    public UserService(DexaReadService dexaRead, DexaDbConnection dexaDb, ILogger<UserService> logger)
    {
        _dexaRead = dexaRead;
        _dexaDb = dexaDb;
        _logger = logger;
    }

    /// <summary>
    /// DEXA DB 직접 로그인 (SHA256 해시 비교, salt 없음)
    /// </summary>
    public async Task<LoginResult> LoginAsync(string userName, string password)
    {
        try
        {
            using var conn = _dexaDb.Create();
            var user = await conn.QueryFirstOrDefaultAsync<UserWithPassword>(
                "SELECT id, userName AS Name, password AS Password, isAdmin AS Admin FROM [user] WHERE userName = @UserName",
                new { UserName = userName });

            if (user == null)
                return new LoginResult { Success = false, Message = "사용자를 찾을 수 없습니다." };

            var inputHash = ComputeSha256(password);
            if (!string.Equals(user.Password, inputHash, StringComparison.OrdinalIgnoreCase))
                return new LoginResult { Success = false, Message = "비밀번호가 일치하지 않습니다." };

            _logger.LogInformation("로그인 성공: {UserName} (Admin: {IsAdmin})", userName, user.Admin);

            return new LoginResult
            {
                Success = true,
                User = new LoginUser
                {
                    Id = user.Id,
                    Name = user.Name,
                    Admin = user.Admin,
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "로그인 실패: {UserName}", userName);
            return new LoginResult { Success = false, Message = $"인증 오류: {ex.Message}" };
        }
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    private class UserWithPassword
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Password { get; set; }
        public bool Admin { get; set; }
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
