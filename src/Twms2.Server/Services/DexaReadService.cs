using System.Data;
using Dapper;
using Twms2.Server.Data;
using Twms2.Server.HOCON;
using Twms2.Server.Models.Dexa;
using Microsoft.Extensions.Caching.Memory;

namespace Twms2.Server.Services;

/// <summary>
/// DEXA DB 직접 접근 서비스 (Dapper). SQLite / MSSQL 자동 대응.
/// 읽기: 자산/스케줄/에이전트 등 조회.
/// 쓰기: parameter 내 description 등 경량 필드 직접 갱신.
/// 실행 작업(백업 트리거 등)은 Akka 메시징(DexaServerClient)을 사용.
/// 자주 조회되는 데이터는 IMemoryCache로 캐싱하여 중복 DB 접근 방지.
/// </summary>
public class DexaReadService
{
    private readonly DexaDbConnection _dexaDb;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DexaReadService> _logger;
    private bool IsSqlServer => _dexaDb.Provider == DexaDbProvider.SqlServer;

    private static readonly TimeSpan AssetCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActionCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AgentCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AssetStatusCacheTtl = TimeSpan.FromSeconds(15);
    private const string CacheKeyAssets = "dexa_view_assets";
    private const string CacheKeyAgents = "dexa_agents";
    private const string CacheKeyLatestActions = "dexa_latest_actions";
    private const string CacheKeyAssetStatuses = "dexa_asset_statuses";

    /// <summary>
    /// 삭제되지 않은 자산 조회 기본 SQL.
    /// asset + assetType 직접 JOIN. aug* 컬럼 미사용 (DEXA 버전별 존재 여부 불확실).
    /// 연결 정보(IP/Base/Slot 등)는 HOCON parameter 파싱 또는 TwmsAsset.ApplyAug()로 채움.
    /// WHERE 절 추가 시 "AND a.컬럼 = ..." 형태로 사용.
    /// </summary>
    private const string ActiveAssetSql = """
        SELECT a.id                AS assetId,
               a.parentId          AS assetParentId,
               a.agentPreferences  AS assetAgentPreferences,
               a.parameter         AS assetParameter,
               a.deleted           AS assetDeleted,
               at.id               AS assetTypeId,
               at.userFriendlyName AS assetTypeUserFriendlyName,
               at.fake             AS assetTypeFake
        FROM asset a
        JOIN assetType at ON a.assetTypeId = at.id
        WHERE a.deleted = 0
        """;

    public DexaReadService(DexaDbConnection dexaDb, IMemoryCache cache, ILogger<DexaReadService> logger)
    {
        _dexaDb = dexaDb;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>자산/액션/에이전트 캐시 일괄 무효화 (쓰기 후 호출)</summary>
    public void InvalidateCache()
    {
        _cache.Remove(CacheKeyAssets);
        _cache.Remove(CacheKeyAgents);
        _cache.Remove(CacheKeyLatestActions);
        // action 캐시(GetRecentActionsAsync)는 key에 limit 포함 → 패턴 삭제 불가이므로 짧은 TTL로 대체
    }

    // ── 자산 ──

    /// <summary>전체 자산 목록 조회. 30초 캐시로 중복 조회 방지.</summary>
    public async Task<List<ViewAsset>> GetViewAssetsAsync()
    {
        if (_cache.TryGetValue(CacheKeyAssets, out List<ViewAsset>? cached))
            return cached!;

        try
        {
            using var conn = _dexaDb.Create();
            var list = (await conn.QueryAsync<ViewAsset>(ActiveAssetSql)).ToList();
            _cache.Set(CacheKeyAssets, list, AssetCacheTtl);
            _logger.LogInformation("DEXA DB 자산 조회: {Count}건", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 자산 조회 실패");
            return [];
        }
    }

    /// <summary>단일 자산 조회. 캐시 우선, 미스 시 단건 SQL.</summary>
    public async Task<ViewAsset?> GetViewAssetAsync(int assetId)
    {
        if (_cache.TryGetValue(CacheKeyAssets, out List<ViewAsset>? cached))
            return cached!.FirstOrDefault(a => a.AssetId == assetId);

        try
        {
            using var conn = _dexaDb.Create();
            return await conn.QueryFirstOrDefaultAsync<ViewAsset>(
                ActiveAssetSql + " AND a.id = @AssetId", new { AssetId = assetId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 자산 단건 조회 실패: AssetId={AssetId}", assetId);
            return null;
        }
    }

    /// <summary>
    /// 자산 타입 목록 조회
    /// </summary>
    public async Task<List<AssetType>> GetAssetTypesAsync()
    {
        try
        {
            using var conn = _dexaDb.Create();
            var result = await conn.QueryAsync<AssetType>(
                "SELECT id, guid, userFriendlyName, fake, icon, parameter, dotnetClassName, description FROM assetType");
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 자산 타입 조회 실패");
            return [];
        }
    }

    // ── 스케줄 / 트리거 ──

    /// <summary>
    /// 트리거 목록 조회 (삭제되지 않은 것만)
    /// </summary>
    public async Task<List<Trigger>> GetTriggersAsync()
    {
        try
        {
            using var conn = _dexaDb.Create();
            var result = await conn.QueryAsync<Trigger>(
                "SELECT id, name, cronSpec AS CronExpression, enable AS Enabled, description FROM [trigger] WHERE deleted = 0");
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 트리거 조회 실패");
            return [];
        }
    }

    /// <summary>
    /// 스케줄 목록 조회 (삭제되지 않은 것만)
    /// </summary>
    public async Task<List<Schedule>> GetSchedulesAsync()
    {
        try
        {
            using var conn = _dexaDb.Create();
            var result = await conn.QueryAsync<Schedule>(
                "SELECT id, assetId, triggerId FROM schedule WHERE deleted = 0");
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 스케줄 조회 실패");
            return [];
        }
    }

    public async Task AddScheduleAsync(int triggerId, int assetId)
    {
        using var conn = _dexaDb.CreateReadWrite();
        var sql = IsSqlServer
            ? """
              MERGE INTO schedule AS t
              USING (SELECT @TriggerId AS triggerId, @AssetId AS assetId) AS s
              ON t.triggerId = s.triggerId AND t.assetId = s.assetId
              WHEN MATCHED THEN UPDATE SET deleted = 0
              WHEN NOT MATCHED THEN INSERT (triggerId, assetId, deleted) VALUES (s.triggerId, s.assetId, 0);
              """
            : """
              INSERT INTO schedule (triggerId, assetId, deleted) VALUES (@TriggerId, @AssetId, 0)
              ON CONFLICT (triggerId, assetId) DO UPDATE SET deleted = 0
              """;
        await conn.ExecuteAsync(sql, new { TriggerId = triggerId, AssetId = assetId });
    }

    public async Task RemoveScheduleAsync(int triggerId, int assetId)
    {
        using var conn = _dexaDb.CreateReadWrite();
        await conn.ExecuteAsync(
            "UPDATE schedule SET deleted = 1 WHERE triggerId = @TriggerId AND assetId = @AssetId AND deleted = 0",
            new { TriggerId = triggerId, AssetId = assetId });
    }

    // ── 사용자 / 권한 ──

    /// <summary>
    /// 사용자 목록 조회 (비밀번호 제외)
    /// </summary>
    public async Task<List<User>> GetUsersAsync()
    {
        try
        {
            using var conn = _dexaDb.Create();
            var result = await conn.QueryAsync<User>(
                "SELECT id, username AS Name, isAdmin AS Admin FROM [user]");
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 사용자 조회 실패");
            return [];
        }
    }

    /// <summary>
    /// 권한 목록 조회
    /// </summary>
    public async Task<List<Permission>> GetPermissionsAsync()
    {
        try
        {
            using var conn = _dexaDb.Create();
            var result = await conn.QueryAsync<Permission>(
                "SELECT id, uid AS UserId, assetId FROM permission");
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 권한 조회 실패");
            return [];
        }
    }

    // ── 이력 / 로그 ──

    /// <summary>
    /// 자산별 마지막 백업 액션 조회 (15초 캐시).
    /// 자산 상태 판단용 — 자산당 1건만 반환하므로 누락 없음.
    /// </summary>
    public async Task<List<DexaAction>> GetLatestActionPerAssetAsync()
    {
        if (_cache.TryGetValue(CacheKeyLatestActions, out List<DexaAction>? cached))
            return cached!;

        try
        {
            using var conn = _dexaDb.Create();
            var result = await conn.QueryAsync<DexaAction>("""
                SELECT ab.id, acs.assetId, acs.version, ab.started, ab.finished,
                       acs.contentsChanged, acs.nthSucceeded, ab.memo
                FROM [action.base] ab
                INNER JOIN [action.schedule] acs ON acs.actionId = ab.id
                INNER JOIN (
                    SELECT assetId, MAX(actionId) AS maxId
                    FROM [action.schedule]
                    GROUP BY assetId
                ) latest ON acs.actionId = latest.maxId
                """);
            var list = result.ToList();
            _cache.Set(CacheKeyLatestActions, list, ActionCacheTtl);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 자산별 최신 액션 조회 실패");
            return [];
        }
    }

    /// <summary>
    /// 최근 백업 액션 이력 조회 (15초 캐시). 최근 활동 목록 표시용.
    /// </summary>
    public async Task<List<DexaAction>> GetRecentActionsAsync(int limit = 100)
    {
        var cacheKey = $"dexa_actions_{limit}";
        if (_cache.TryGetValue(cacheKey, out List<DexaAction>? cached))
            return cached!;

        try
        {
            using var conn = _dexaDb.Create();
            var sql = IsSqlServer
                ? """
                  SELECT ab.id, acs.assetId, acs.version, ab.started, ab.finished,
                         acs.contentsChanged, acs.nthSucceeded, ab.memo
                  FROM [action.base] ab
                  INNER JOIN [action.schedule] acs ON acs.actionId = ab.id
                  ORDER BY ab.started DESC OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY
                  """
                : """
                  SELECT ab.id, acs.assetId, acs.version, ab.started, ab.finished,
                         acs.contentsChanged, acs.nthSucceeded, ab.memo
                  FROM [action.base] ab
                  INNER JOIN [action.schedule] acs ON acs.actionId = ab.id
                  ORDER BY ab.started DESC LIMIT @Limit
                  """;
            var result = await conn.QueryAsync<DexaAction>(sql, new { Limit = limit });
            var list = result.ToList();
            _cache.Set(cacheKey, list, ActionCacheTtl);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 액션 이력 조회 실패");
            return [];
        }
    }

    /// <summary>
    /// 특정 액션의 상세 로그 조회. 기본 200건, loadAll=true 시 전체.
    /// </summary>
    public async Task<(List<ActionLog> Logs, int TotalCount)> GetActionLogsAsync(
        int actionId, int limit = 200, bool loadAll = false)
    {
        try
        {
            using var conn = _dexaDb.Create();

            var totalCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM actionLog WHERE actionId = @ActionId",
                new { ActionId = actionId });

            string sql;
            if (loadAll)
                sql = "SELECT id, actionId, level, message, dateTime FROM actionLog WHERE actionId = @ActionId ORDER BY dateTime";
            else if (IsSqlServer)
                sql = "SELECT id, actionId, level, message, dateTime FROM actionLog WHERE actionId = @ActionId ORDER BY dateTime OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY";
            else
                sql = "SELECT id, actionId, level, message, dateTime FROM actionLog WHERE actionId = @ActionId ORDER BY dateTime LIMIT @Limit";

            var result = await conn.QueryAsync<ActionLog>(sql,
                new { ActionId = actionId, Limit = limit });

            return (result.ToList(), totalCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 액션 로그 조회 실패: ActionId={ActionId}", actionId);
            return ([], 0);
        }
    }

    /// <summary>
    /// 날짜 범위별 백업 액션 이력 조회.
    /// </summary>
    public async Task<List<DexaAction>> GetActionsInRangeAsync(DateTime from, DateTime to)
    {
        try
        {
            using var conn = _dexaDb.Create();
            var p = new DynamicParameters();
            p.Add("From", from, DbType.DateTime2);
            p.Add("To", to, DbType.DateTime2);
            var result = await conn.QueryAsync<DexaAction>("""
                SELECT ab.id, acs.assetId, acs.version, ab.started, ab.finished,
                       acs.contentsChanged, acs.nthSucceeded, ab.memo
                FROM [action.base] ab
                INNER JOIN [action.schedule] acs ON acs.actionId = ab.id
                WHERE ab.started >= @From AND ab.started < @To
                ORDER BY ab.started DESC
                """, p);
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 날짜별 액션 조회 실패");
            return [];
        }
    }

    public async Task<List<DexaAction>> GetAllActionsAsync()
    {
        try
        {
            using var conn = _dexaDb.Create();
            var result = await conn.QueryAsync<DexaAction>("""
                SELECT ab.id, acs.assetId, acs.version, ab.started, ab.finished,
                       acs.contentsChanged, acs.nthSucceeded, ab.memo
                FROM [action.base] ab
                INNER JOIN [action.schedule] acs ON acs.actionId = ab.id
                ORDER BY ab.started DESC
                """);
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 전체 액션 조회 실패");
            return [];
        }
    }

    // ── 에이전트 ──

    /// <summary>
    /// 에이전트 목록 조회 (15초 캐시).
    /// </summary>
    public async Task<List<Agent>> GetAgentsAsync()
    {
        if (_cache.TryGetValue(CacheKeyAgents, out List<Agent>? cached))
            return cached!;

        try
        {
            using var conn = _dexaDb.Create();
            var result = await conn.QueryAsync<Agent>(
                "SELECT id, name, ip, swVersion, online, connected, disconnected FROM agent ORDER BY name");
            var list = result.ToList();
            _cache.Set(CacheKeyAgents, list, AgentCacheTtl);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEXA DB 에이전트 목록 조회 실패");
            return [];
        }
    }

    // ── 에셋 상태 (2.20) ──

    /// <summary>
    /// 에셋 실시간 상태 목록 조회 (asset_status 테이블, 15초 캐시).
    /// PLC Trigger/Topic 시스템이 주기적으로 갱신하는 데이터.
    /// </summary>
    public async Task<List<AssetStatus>> GetAssetStatusesAsync()
    {
        if (_cache.TryGetValue(CacheKeyAssetStatuses, out List<AssetStatus>? cached))
            return cached!;

        try
        {
            using var conn = _dexaDb.Create();
            var result = await conn.QueryAsync<AssetStatus>(
                "SELECT assetId, available, time, status, detail FROM [asset.status]");
            var list = result.ToList();
            _cache.Set(CacheKeyAssetStatuses, list, AssetStatusCacheTtl);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DEXA DB asset_status 조회 실패 (테이블 미존재 가능)");
            return [];
        }
    }

    /// <summary>
    /// 특정 에셋의 실시간 상태 조회.
    /// </summary>
    public async Task<AssetStatus?> GetAssetStatusAsync(int assetId)
    {
        var all = await GetAssetStatusesAsync();
        return all.FirstOrDefault(s => s.AssetId == assetId);
    }

    // ── 쓰기 ──

    /// <summary>
    /// DEXA 자산의 parameter 내 description 값을 직접 갱신.
    /// </summary>
    public async Task UpdateAssetDescriptionAsync(int assetId, string description)
    {
        using var conn = _dexaDb.CreateReadWrite();

        var currentParam = await conn.ExecuteScalarAsync<string?>(
            "SELECT parameter FROM asset WHERE id = @Id", new { Id = assetId });

        if (string.IsNullOrEmpty(currentParam))
        {
            _logger.LogWarning("자산 {AssetId} parameter가 비어있어 description 갱신 불가", assetId);
            return;
        }

        string updatedParam;
        try
        {
            var hoconParam = new Parameter(currentParam);
            var descItem = hoconParam.Items.FirstOrDefault(it => it.Key == "description");
            if (descItem != null)
            {
                descItem.Value = description;
                var tpls = hoconParam.Items.SelectMany(it => it.ToKeyValuePairs());
                updatedParam = Parameter.Buildup(tpls);
            }
            else
            {
                _logger.LogWarning("자산 {AssetId} parameter에 description 항목 없음", assetId);
                return;
            }
        }
        catch
        {
            // HOCON 파싱 실패 시 regex fallback
            updatedParam = ParameterHelper.ReplaceValueFallback(currentParam, @"\.description\.value", description);

            if (updatedParam == currentParam)
            {
                _logger.LogWarning("자산 {AssetId} parameter에서 description 패턴 미발견", assetId);
                return;
            }
        }

        await conn.ExecuteAsync(
            "UPDATE asset SET parameter = @Parameter WHERE id = @Id",
            new { Parameter = updatedParam, Id = assetId });

        InvalidateCache();
        _logger.LogInformation("자산 {AssetId} description 갱신 완료", assetId);
    }

    /// <summary>
    /// DEXA 자산의 parameter 및 agentPreferences를 직접 갱신.
    /// 일괄 수정 등에서 사용.
    /// </summary>
    public async Task UpdateAssetAsync(int assetId, string parameter, string? agentPreferences)
    {
        using var conn = _dexaDb.CreateReadWrite();
        await conn.ExecuteAsync(
            "UPDATE asset SET parameter = @Parameter, agentPreferences = @AgentPreferences WHERE id = @Id",
            new { Parameter = parameter, AgentPreferences = agentPreferences, Id = assetId });
        InvalidateCache();
    }

    /// <summary>
    /// DEXA asset 테이블에서 자산 1건의 parameter, agentPreferences 조회.
    /// </summary>
    public async Task<(string parameter, string? agentPreferences)?> GetAssetRawAsync(int assetId)
    {
        using var conn = _dexaDb.Create();
        var row = await conn.QueryFirstOrDefaultAsync<(string parameter, string? agentPreferences)>(
            "SELECT parameter, agentPreferences FROM asset WHERE id = @Id", new { Id = assetId });
        // Dapper value tuple: 미조회 시 default(ValueTuple) = (null, null)
        return row.parameter != null ? row : null;
    }

    /// <summary>
    /// DEXA asset 테이블에서 여러 자산의 parameter, agentPreferences 일괄 조회.
    /// </summary>
    public async Task<Dictionary<int, (string parameter, string? agentPreferences)>> GetAssetRawBatchAsync(int[] assetIds)
    {
        if (assetIds.Length == 0) return [];
        using var conn = _dexaDb.Create();
        var rows = await conn.QueryAsync<(int id, string parameter, string? agentPreferences)>(
            "SELECT id, parameter, agentPreferences FROM asset WHERE id IN @Ids",
            new { Ids = assetIds });
        return rows.Where(r => r.parameter != null)
                   .ToDictionary(r => r.id, r => (r.parameter, r.agentPreferences));
    }
}
