using Dapper;
using Twms2.Server.Data;
using Twms2.Server.Models.Twm;
using Microsoft.Extensions.Caching.Memory;

namespace Twms2.Server.Services;

/// <summary>
/// TWM 로컬 DB 서비스 — 자산 보강 정보 (TwmsAsset, TwmsAssetConn) + 통계/마이그레이션.
/// 레이아웃 관련 → LayoutDbService, 핑 → PingDbService, 매뉴얼 → ManualDbService.
/// </summary>
public class TwmDbService
{
    private readonly TwmDbConnection _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TwmDbService> _logger;

    private static readonly TimeSpan AugCacheTtl = TimeSpan.FromSeconds(30);

    private const string CacheKeyAugMap  = "twm_aug";
    private const string CacheKeyConnMap = "twm_conn";

    public TwmDbService(TwmDbConnection db, IMemoryCache cache, ILogger<TwmDbService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public void InvalidateAugCache()
    {
        _cache.Remove(CacheKeyAugMap);
        _cache.Remove(CacheKeyConnMap);
    }

    // ──────────────── TwmsAsset ────────────────

    public async Task<TwmsAsset?> GetTwmsAssetAsync(int dexaId)
    {
        using var conn = _db.Create();
        return await conn.QueryFirstOrDefaultAsync<TwmsAsset>(
            "SELECT * FROM TwmsAsset WHERE DexaId = @Id", new { Id = dexaId });
    }

    public async Task<Dictionary<int, TwmsAsset>> GetTwmsAssetMapAsync()
    {
        if (_cache.TryGetValue(CacheKeyAugMap, out Dictionary<int, TwmsAsset>? cached))
            return cached!;

        using var conn = _db.Create();
        var list = (await conn.QueryAsync<TwmsAsset>("SELECT * FROM TwmsAsset")).AsList();
        var map = list.ToDictionary(a => a.DexaId);
        _cache.Set(CacheKeyAugMap, map, AugCacheTtl);
        return map;
    }

    public async Task UpsertTwmsAssetAsync(TwmsAsset asset)
    {
        InvalidateAugCache();
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsAsset (DexaId, AugStationNumber, AugVendor, AugSpec, AugLineId, UpdatedAt)
            VALUES (@DexaId, @AugStationNumber, @AugVendor, @AugSpec, @AugLineId, CURRENT_TIMESTAMP)
            ON CONFLICT(DexaId) DO UPDATE SET
                AugStationNumber = @AugStationNumber,
                AugVendor        = @AugVendor,
                AugSpec          = @AugSpec,
                AugLineId        = @AugLineId,
                UpdatedAt        = CURRENT_TIMESTAMP
            """, asset);
    }

    public async Task UpdateAssetLineIdAsync(int dexaId, int? lineId)
    {
        InvalidateAugCache();
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsAsset (DexaId, AugLineId, UpdatedAt)
            VALUES (@DexaId, @LineId, CURRENT_TIMESTAMP)
            ON CONFLICT(DexaId) DO UPDATE SET
                AugLineId = @LineId,
                UpdatedAt = CURRENT_TIMESTAMP
            """, new { DexaId = dexaId, LineId = lineId });
    }

    public async Task DeleteTwmsAssetAsync(int dexaId)
    {
        InvalidateAugCache();
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsAsset WHERE DexaId = @Id", new { Id = dexaId });
    }

    // ──────────────── TwmsAssetConn ────────────────

    public async Task<TwmsAssetConn?> GetTwmsAssetConnAsync(int dexaId)
    {
        using var conn = _db.Create();
        return await conn.QueryFirstOrDefaultAsync<TwmsAssetConn>(
            "SELECT * FROM TwmsAssetConn WHERE DexaId = @Id", new { Id = dexaId });
    }

    public async Task<Dictionary<int, TwmsAssetConn>> GetTwmsAssetConnMapAsync()
    {
        if (_cache.TryGetValue(CacheKeyConnMap, out Dictionary<int, TwmsAssetConn>? cached))
            return cached!;

        using var conn = _db.Create();
        var list = (await conn.QueryAsync<TwmsAssetConn>("SELECT * FROM TwmsAssetConn")).AsList();
        var map = list.ToDictionary(a => a.DexaId);
        _cache.Set(CacheKeyConnMap, map, AugCacheTtl);
        return map;
    }

    public async Task UpsertTwmsAssetConnAsync(TwmsAssetConn connInfo)
    {
        InvalidateAugCache();
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsAssetConn (DexaId, AugIp, AugIpVia, AugBaseNumber, AugSlotNumber, AugIsRobotPLC, UpdatedAt)
            VALUES (@DexaId, @AugIp, @AugIpVia, @AugBaseNumber, @AugSlotNumber, @AugIsRobotPLC, CURRENT_TIMESTAMP)
            ON CONFLICT(DexaId) DO UPDATE SET
                AugIp         = @AugIp,
                AugIpVia      = @AugIpVia,
                AugBaseNumber = @AugBaseNumber,
                AugSlotNumber = @AugSlotNumber,
                AugIsRobotPLC = @AugIsRobotPLC,
                UpdatedAt     = CURRENT_TIMESTAMP
            """, connInfo);
    }

    public async Task DeleteTwmsAssetConnAsync(int dexaId)
    {
        InvalidateAugCache();
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsAssetConn WHERE DexaId = @Id", new { Id = dexaId });
    }

    // ──────────────── TwmMigration ────────────────

    public async Task<List<TwmMigration>> GetMigrationsAsync()
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmMigration>(
            "SELECT * FROM TwmMigration ORDER BY Version")).AsList();
    }

    // ──────────────── DB 통계 ────────────────

    public async Task<TwmDbStats> GetStatsAsync()
    {
        using var conn = _db.Create();
        var currentVersion = await conn.ExecuteScalarAsync<int?>("SELECT MAX(Version) FROM TwmMigration") ?? 0;
        var assetCount     = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM TwmsAsset");
        var connCount      = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM TwmsAssetConn");

        var lineCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TwmsLayoutLine");
        var groupCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TwmsLayoutGroup");

        return new TwmDbStats
        {
            SchemaVersion   = currentVersion,
            AssetAugCount   = assetCount,
            AssetConnCount  = connCount,
            LayoutLineCount = lineCount,
            LayoutGroupCount = groupCount,
        };
    }
}
