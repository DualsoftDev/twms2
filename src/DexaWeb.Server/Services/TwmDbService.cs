using Dapper;
using DexaWeb.Server.Data;
using DexaWeb.Server.Models.Twm;
using Microsoft.Extensions.Caching.Memory;

namespace DexaWeb.Server.Services;

/// <summary>
/// TWM 로컬 DB 서비스.
/// TwmsAsset (공통 정보), TwmsAssetConn (PLC/Servo 연결정보) CRUD.
/// Ping 결과는 IMemoryCache 인메모리 저장 (재시작 시 초기화).
/// </summary>
public class TwmDbService
{
    private readonly TwmDbConnection _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TwmDbService> _logger;

    private static readonly TimeSpan AugCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingCacheTtl = TimeSpan.FromMinutes(10);

    private const string CacheKeyAugMap  = "twm_aug";
    private const string CacheKeyConnMap = "twm_conn";
    private const string CacheKeyPing    = "twm_ping";

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

    // ──────────────── TwmsLayoutLine ────────────────

    public async Task<Dictionary<int, string>> GetTwmsLayoutLineMapAsync()
    {
        using var conn = _db.Create();
        var list = (await conn.QueryAsync<TwmsLayoutLine>("SELECT * FROM TwmsLayoutLine")).AsList();
        return list.ToDictionary(l => l.Id, l => l.Name);
    }

    public async Task UpsertTwmsLayoutLineAsync(TwmsLayoutLine line)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsLayoutLine (Id, Name, UpdatedAt)
            VALUES (@Id, @Name, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                Name      = @Name,
                UpdatedAt = CURRENT_TIMESTAMP
            """, line);
    }

    public async Task<List<TwmsLayoutLine>> GetAllTwmsLayoutLinesAsync()
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsLayoutLine>(
            "SELECT * FROM TwmsLayoutLine ORDER BY Id")).AsList();
    }

    public async Task DeleteTwmsLayoutLineAsync(int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsLayoutLine WHERE Id = @Id", new { Id = id });
    }

    public async Task DeleteAllTwmsLayoutLinesAsync()
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsLayoutLine");
    }

    public async Task<int> CountAssetsByLineIdAsync(int lineId)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TwmsAsset WHERE AugLineId = @LineId", new { LineId = lineId });
    }

    // ──────────────── TwmsLayoutGroup ────────────────

    public async Task UpsertTwmsLayoutGroupAsync(TwmsLayoutGroup group)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsLayoutGroup (Id, AssetId, Floor, Assets, UpdatedAt)
            VALUES (@Id, @AssetId, @Floor, @Assets, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                AssetId   = @AssetId,
                Floor     = @Floor,
                Assets    = @Assets,
                UpdatedAt = CURRENT_TIMESTAMP
            """, group);
    }

    public async Task<List<TwmsLayoutGroup>> GetAllTwmsLayoutGroupsAsync()
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsLayoutGroup>(
            "SELECT * FROM TwmsLayoutGroup ORDER BY Id")).AsList();
    }

    public async Task DeleteAllTwmsLayoutGroupsAsync()
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsLayoutGroup");
    }

    // ──────────────── TwmsLayout ────────────────

    public async Task<List<TwmsLayout>> GetAllLayoutsAsync()
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsLayout>(
            "SELECT * FROM TwmsLayout ORDER BY SortOrder, Id")).AsList();
    }

    public async Task<int> InsertLayoutAsync(TwmsLayout layout)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO TwmsLayout (Name, SortOrder, UpdatedAt)
            VALUES (@Name, @SortOrder, CURRENT_TIMESTAMP);
            SELECT last_insert_rowid();
            """, layout);
    }

    public async Task UpdateLayoutAsync(TwmsLayout layout)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            UPDATE TwmsLayout SET Name = @Name, SortOrder = @SortOrder,
                UpdatedAt = CURRENT_TIMESTAMP WHERE Id = @Id
            """, layout);
    }

    public async Task DeleteLayoutAsync(int layoutId)
    {
        using var conn = _db.Create();
        // 하위 테이블 직접 삭제 (SQLite FK cascade 비활성 대비)
        await conn.ExecuteAsync("DELETE FROM TwmsBlueprintConfig WHERE LayoutId = @Id", new { Id = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsBlueprintRect WHERE LayoutId = @Id", new { Id = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsAssetPosition WHERE LayoutId = @Id", new { Id = layoutId });
        await conn.ExecuteAsync("""
            DELETE FROM TwmsPlacementGroupMember WHERE GroupId IN
                (SELECT Id FROM TwmsPlacementGroup WHERE LayoutId = @Id)
            """, new { Id = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroup WHERE LayoutId = @Id", new { Id = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsLayout WHERE Id = @Id", new { Id = layoutId });
    }

    public async Task<int> DuplicateLayoutAsync(int sourceLayoutId, string newName)
    {
        using var conn = _db.Create();
        using var tx = conn.BeginTransaction();
        try
        {
            var newId = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO TwmsLayout (Name, SortOrder, UpdatedAt)
                VALUES (@Name, (SELECT COALESCE(MAX(SortOrder),0)+1 FROM TwmsLayout), CURRENT_TIMESTAMP);
                SELECT last_insert_rowid();
                """, new { Name = newName }, transaction: tx);

            await conn.ExecuteAsync("""
                INSERT INTO TwmsBlueprintConfig (LayoutId, ImagePath, ImageWidth, ImageHeight, DrawingData, BgColor, GridColor, UpdatedAt)
                SELECT @NewId, ImagePath, ImageWidth, ImageHeight, DrawingData, BgColor, GridColor, CURRENT_TIMESTAMP
                FROM TwmsBlueprintConfig WHERE LayoutId = @SrcId
                """, new { NewId = newId, SrcId = sourceLayoutId }, transaction: tx);

            await conn.ExecuteAsync("""
                INSERT INTO TwmsBlueprintRect (LayoutId, LineId, X, Y, Width, Height, UpdatedAt)
                SELECT @NewId, LineId, X, Y, Width, Height, CURRENT_TIMESTAMP
                FROM TwmsBlueprintRect WHERE LayoutId = @SrcId
                """, new { NewId = newId, SrcId = sourceLayoutId }, transaction: tx);

            await conn.ExecuteAsync("""
                INSERT INTO TwmsAssetPosition (LayoutId, AssetId, X, Y, Scale, Visible, UpdatedAt)
                SELECT @NewId, AssetId, X, Y, Scale, Visible, CURRENT_TIMESTAMP
                FROM TwmsAssetPosition WHERE LayoutId = @SrcId
                """, new { NewId = newId, SrcId = sourceLayoutId }, transaction: tx);

            // 배치 그룹 복제
            var srcGroups = (await conn.QueryAsync<TwmsPlacementGroup>(
                "SELECT * FROM TwmsPlacementGroup WHERE LayoutId = @SrcId",
                new { SrcId = sourceLayoutId }, transaction: tx)).AsList();
            foreach (var g in srcGroups)
            {
                var newGid = await conn.ExecuteScalarAsync<int>("""
                    INSERT INTO TwmsPlacementGroup (LayoutId, Name, X, Y, Width, Height, Color, UpdatedAt)
                    VALUES (@LayoutId, @Name, @X, @Y, @Width, @Height, @Color, CURRENT_TIMESTAMP);
                    SELECT last_insert_rowid();
                    """, new { LayoutId = newId, g.Name, g.X, g.Y, g.Width, g.Height, g.Color }, transaction: tx);
                await conn.ExecuteAsync("""
                    INSERT INTO TwmsPlacementGroupMember (GroupId, AssetId)
                    SELECT @NewGid, AssetId FROM TwmsPlacementGroupMember WHERE GroupId = @OldGid
                    """, new { NewGid = newGid, OldGid = g.Id }, transaction: tx);
            }

            tx.Commit();
            return newId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ──────────────── TwmsBlueprintRect / Config (레이아웃별) ────────────────

    public async Task<List<TwmsBlueprintRect>> GetAllBlueprintRectsAsync(int layoutId)
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsBlueprintRect>(
            "SELECT * FROM TwmsBlueprintRect WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId })).AsList();
    }

    public async Task UpsertBlueprintRectAsync(TwmsBlueprintRect rect)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsBlueprintRect (LayoutId, LineId, X, Y, Width, Height, UpdatedAt)
            VALUES (@LayoutId, @LineId, @X, @Y, @Width, @Height, CURRENT_TIMESTAMP)
            ON CONFLICT(LayoutId, LineId) DO UPDATE SET
                X = @X, Y = @Y, Width = @Width, Height = @Height,
                UpdatedAt = CURRENT_TIMESTAMP
            """, rect);
    }

    public async Task DeleteBlueprintRectAsync(int layoutId, int lineId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "DELETE FROM TwmsBlueprintRect WHERE LayoutId = @LayoutId AND LineId = @LineId",
            new { LayoutId = layoutId, LineId = lineId });
    }

    public async Task<TwmsBlueprintConfig?> GetBlueprintConfigAsync(int layoutId)
    {
        using var conn = _db.Create();
        return await conn.QueryFirstOrDefaultAsync<TwmsBlueprintConfig>(
            "SELECT * FROM TwmsBlueprintConfig WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId });
    }

    public async Task UpsertBlueprintConfigAsync(TwmsBlueprintConfig config)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsBlueprintConfig (LayoutId, ImagePath, ImageWidth, ImageHeight, DrawingData, BgColor, GridColor, UpdatedAt)
            VALUES (@LayoutId, @ImagePath, @ImageWidth, @ImageHeight, @DrawingData, @BgColor, @GridColor, CURRENT_TIMESTAMP)
            ON CONFLICT(LayoutId) DO UPDATE SET
                ImagePath   = @ImagePath,
                ImageWidth  = @ImageWidth,
                ImageHeight = @ImageHeight,
                DrawingData = @DrawingData,
                BgColor     = @BgColor,
                GridColor   = @GridColor,
                UpdatedAt   = CURRENT_TIMESTAMP
            """, config);
    }

    // ──────────────── TwmsAssetPosition (레이아웃별) ────────────────

    public async Task<List<TwmsAssetPosition>> GetAllAssetPositionsAsync(int layoutId)
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsAssetPosition>(
            "SELECT * FROM TwmsAssetPosition WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId })).AsList();
    }

    public async Task UpsertAssetPositionAsync(TwmsAssetPosition pos)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsAssetPosition (LayoutId, AssetId, X, Y, Scale, Visible, UpdatedAt)
            VALUES (@LayoutId, @AssetId, @X, @Y, @Scale, @Visible, CURRENT_TIMESTAMP)
            ON CONFLICT(LayoutId, AssetId) DO UPDATE SET
                X = @X, Y = @Y, Scale = @Scale, Visible = @Visible,
                UpdatedAt = CURRENT_TIMESTAMP
            """, pos);
    }

    public async Task UpsertAssetPositionBatchAsync(List<TwmsAssetPosition> positions)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsAssetPosition (LayoutId, AssetId, X, Y, Scale, Visible, UpdatedAt)
            VALUES (@LayoutId, @AssetId, @X, @Y, @Scale, @Visible, CURRENT_TIMESTAMP)
            ON CONFLICT(LayoutId, AssetId) DO UPDATE SET
                X = @X, Y = @Y, Scale = @Scale, Visible = @Visible,
                UpdatedAt = CURRENT_TIMESTAMP
            """, positions);
    }

    // ──────────────── Ping 결과 (인메모리 + DB 영속화) ────────────────

    public Task UpsertPingResultAsync(int dexaAssetId, string? ipAddress, bool reachable, int? roundtripMs)
    {
        var map = _cache.GetOrCreate(CacheKeyPing,
            _ => new Dictionary<int, TwmPingResult>())!;

        map[dexaAssetId] = new TwmPingResult
        {
            DexaAssetId = dexaAssetId,
            IpAddress   = ipAddress,
            Reachable   = reachable,
            RoundtripMs = roundtripMs,
            CheckedAt   = DateTime.Now,
        };
        return Task.CompletedTask;
    }

    public Task<List<TwmPingResult>> GetAllPingResultsAsync()
    {
        if (_cache.TryGetValue(CacheKeyPing, out Dictionary<int, TwmPingResult>? map))
            return Task.FromResult(map!.Values.ToList());
        return Task.FromResult(new List<TwmPingResult>());
    }

    /// <summary>서버 시작 시 DB → 캐시 로드 (이전 핑 결과 복원).</summary>
    public async Task LoadPingCacheFromDbAsync()
    {
        try
        {
            using var conn = _db.Create();
            var list = (await conn.QueryAsync<TwmPingResult>(
                "SELECT DexaAssetId, IpAddress, Reachable, RoundtripMs, CheckedAt FROM TwmsPingResult"
            )).AsList();

            if (list.Count == 0) return;

            var map = _cache.GetOrCreate(CacheKeyPing,
                _ => new Dictionary<int, TwmPingResult>())!;
            foreach (var r in list)
                map[r.DexaAssetId] = r;

            _logger.LogInformation("DB에서 핑 결과 {Count}건 캐시 로드 완료", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB에서 핑 결과 로드 실패 (첫 실행이면 정상)");
        }
    }

    /// <summary>
    /// 캐시 핑 결과를 DB에 배치 저장 + 상태 변경 이력 기록.
    /// 핑 사이클 완료 후 호출.
    /// </summary>
    public async Task FlushPingToDbAsync()
    {
        if (!_cache.TryGetValue(CacheKeyPing, out Dictionary<int, TwmPingResult>? map) || map!.Count == 0)
            return;

        var currentResults = map.Values.ToList();

        using var conn = _db.Create();
        using var tx = conn.BeginTransaction();

        try
        {
            // 이전 상태 조회 (상태 변경 감지용)
            var prevMap = (await conn.QueryAsync<TwmPingResult>(
                "SELECT DexaAssetId, Reachable FROM TwmsPingResult", transaction: tx
            )).ToDictionary(r => r.DexaAssetId, r => r.Reachable);

            // 상태 변경 이력 기록
            var logs = new List<TwmPingLog>();
            foreach (var r in currentResults)
            {
                if (!prevMap.TryGetValue(r.DexaAssetId, out var wasReachable) || wasReachable != r.Reachable)
                {
                    logs.Add(new TwmPingLog
                    {
                        DexaAssetId = r.DexaAssetId,
                        Reachable   = r.Reachable,
                        CheckedAt   = r.CheckedAt,
                    });
                }
            }

            if (logs.Count > 0)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO TwmsPingLog (DexaAssetId, Reachable, CheckedAt)
                    VALUES (@DexaAssetId, @Reachable, @CheckedAt)
                    """, logs, transaction: tx);
            }

            // 최신 상태 배치 UPSERT
            await conn.ExecuteAsync("""
                INSERT INTO TwmsPingResult (DexaAssetId, IpAddress, Reachable, RoundtripMs, CheckedAt)
                VALUES (@DexaAssetId, @IpAddress, @Reachable, @RoundtripMs, @CheckedAt)
                ON CONFLICT(DexaAssetId) DO UPDATE SET
                    IpAddress   = @IpAddress,
                    Reachable   = @Reachable,
                    RoundtripMs = @RoundtripMs,
                    CheckedAt   = @CheckedAt
                """, currentResults, transaction: tx);

            tx.Commit();

            if (logs.Count > 0)
                _logger.LogInformation("핑 DB 동기화: {Total}건 저장, 상태 변경 {Changed}건 기록",
                    currentResults.Count, logs.Count);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "핑 결과 DB 저장 실패");
        }
    }

    // ──────────────── TwmsPingLog 조회 ────────────────

    public async Task<List<TwmPingLog>> GetPingLogsAsync(DateTime start, DateTime end)
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmPingLog>(
            """
            SELECT Id, DexaAssetId, Reachable, CheckedAt
            FROM TwmsPingLog
            WHERE CheckedAt >= @Start AND CheckedAt < @End
            ORDER BY CheckedAt DESC
            """, new { Start = start, End = end.AddDays(1) }
        )).AsList();
    }

    // ──────────────── TwmMigration ────────────────

    public async Task<List<TwmMigration>> GetMigrationsAsync()
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmMigration>(
            "SELECT * FROM TwmMigration ORDER BY Version")).AsList();
    }

    // ──────────────── TwmsManual (매뉴얼) ────────────────

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

    // ──────────────── TwmsPlacementGroup (배치 그룹) ────────────────

    public async Task<List<TwmsPlacementGroup>> GetAllPlacementGroupsAsync(int layoutId)
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsPlacementGroup>(
            "SELECT * FROM TwmsPlacementGroup WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId })).AsList();
    }

    public async Task<List<TwmsPlacementGroupMember>> GetPlacementGroupMembersAsync(int layoutId)
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsPlacementGroupMember>("""
            SELECT m.GroupId, m.AssetId
            FROM TwmsPlacementGroupMember m
            JOIN TwmsPlacementGroup g ON g.Id = m.GroupId
            WHERE g.LayoutId = @LayoutId
            """, new { LayoutId = layoutId })).AsList();
    }

    public async Task<int> InsertPlacementGroupAsync(TwmsPlacementGroup group)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO TwmsPlacementGroup (LayoutId, Name, X, Y, Width, Height, Color, UpdatedAt)
            VALUES (@LayoutId, @Name, @X, @Y, @Width, @Height, @Color, CURRENT_TIMESTAMP);
            SELECT last_insert_rowid();
            """, group);
    }

    public async Task UpsertPlacementGroupAsync(TwmsPlacementGroup group)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsPlacementGroup (Id, LayoutId, Name, X, Y, Width, Height, Color, UpdatedAt)
            VALUES (@Id, @LayoutId, @Name, @X, @Y, @Width, @Height, @Color, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name, X = excluded.X, Y = excluded.Y,
                Width = excluded.Width, Height = excluded.Height,
                Color = excluded.Color, UpdatedAt = CURRENT_TIMESTAMP
            """, group);
    }

    public async Task DeletePlacementGroupAsync(int groupId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroupMember WHERE GroupId = @Id", new { Id = groupId });
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroup WHERE Id = @Id", new { Id = groupId });
    }

    public async Task SetPlacementGroupMembersAsync(int groupId, List<int> assetIds)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroupMember WHERE GroupId = @GroupId",
            new { GroupId = groupId });
        if (assetIds.Count > 0)
            await conn.ExecuteAsync(
                "INSERT INTO TwmsPlacementGroupMember (GroupId, AssetId) VALUES (@GroupId, @AssetId)",
                assetIds.Select(a => new { GroupId = groupId, AssetId = a }));
    }

    public async Task UpsertPlacementGroupBatchAsync(List<TwmsPlacementGroup> groups)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsPlacementGroup (Id, LayoutId, Name, X, Y, Width, Height, Color, UpdatedAt)
            VALUES (@Id, @LayoutId, @Name, @X, @Y, @Width, @Height, @Color, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name, X = excluded.X, Y = excluded.Y,
                Width = excluded.Width, Height = excluded.Height,
                Color = excluded.Color, UpdatedAt = CURRENT_TIMESTAMP
            """, groups);
    }

    public async Task DeleteAllPlacementGroupsAsync(int layoutId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            DELETE FROM TwmsPlacementGroupMember WHERE GroupId IN
                (SELECT Id FROM TwmsPlacementGroup WHERE LayoutId = @LayoutId)
            """, new { LayoutId = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroup WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId });
    }
}
