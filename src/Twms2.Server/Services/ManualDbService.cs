using Dapper;
using Twms2.Server.Data;
using Twms2.Server.Models.Twm;

namespace Twms2.Server.Services;

/// <summary>
/// 매뉴얼 DB 서비스.
/// TwmsManual CRUD.
/// </summary>
public class ManualDbService
{
    private readonly TwmDbConnection _db;

    public ManualDbService(TwmDbConnection db)
    {
        _db = db;
    }

    public async Task<List<TwmsManual>> GetAllManualsAsync()
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsManual>(
            "SELECT * FROM TwmsManual ORDER BY UploadedAt DESC")).AsList();
    }

    public async Task<List<TwmsManual>> GetManualsByKeywordMatchAsync(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return [];

        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsManual>(
            "SELECT * FROM TwmsManual WHERE LOWER(@Spec) LIKE '%' || LOWER(Keyword) || '%' ORDER BY Keyword",
            new { Spec = spec })).AsList();
    }

    public async Task<int> InsertManualAsync(TwmsManual manual)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO TwmsManual (Keyword, FileName, StoredFileName, UploadedAt)
            VALUES (@Keyword, @FileName, @StoredFileName, CURRENT_TIMESTAMP);
            SELECT last_insert_rowid();
            """, manual);
    }

    public async Task<TwmsManual?> GetManualByIdAsync(int id)
    {
        using var conn = _db.Create();
        return await conn.QueryFirstOrDefaultAsync<TwmsManual>(
            "SELECT * FROM TwmsManual WHERE Id = @Id", new { Id = id });
    }

    public async Task DeleteManualAsync(int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsManual WHERE Id = @Id", new { Id = id });
    }
}
