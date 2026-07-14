using Dapper;
using Twms2.Server.Data;
using Twms2.Server.Models.Twm;
using Microsoft.Extensions.Caching.Memory;

namespace Twms2.Server.Services;

/// <summary>
/// Ping 결과 관리 서비스.
/// 인메모리 캐시 + DB 영속화.
/// </summary>
public class PingDbService
{
    private readonly TwmDbConnection _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PingDbService> _logger;

    private const string CacheKeyPing = "twm_ping";

    public PingDbService(TwmDbConnection db, IMemoryCache cache, ILogger<PingDbService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

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
            var prevMap = (await conn.QueryAsync<TwmPingResult>(
                "SELECT DexaAssetId, Reachable FROM TwmsPingResult", transaction: tx
            )).ToDictionary(r => r.DexaAssetId, r => r.Reachable);

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

    /// <summary>자산의 마지막 온라인↔오프라인 상태 전환 기록 (없으면 null).</summary>
    public async Task<TwmPingLog?> GetLastPingChangeAsync(int dexaAssetId)
    {
        using var conn = _db.Create();
        return await conn.QueryFirstOrDefaultAsync<TwmPingLog>(
            """
            SELECT Id, DexaAssetId, Reachable, CheckedAt
            FROM TwmsPingLog
            WHERE DexaAssetId = @Id
            ORDER BY Id DESC
            LIMIT 1
            """, new { Id = dexaAssetId });
    }

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
}
