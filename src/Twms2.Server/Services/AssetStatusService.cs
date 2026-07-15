using Microsoft.Extensions.Caching.Memory;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Models.Dexa;

namespace Twms2.Server.Services;

/// <summary>
/// 대시보드/사이드바 공통 KPI. BackedUp=백업갱신(변경 있음), Failed=총-성공-내역없음.
/// </summary>
public record KpiSummary(int Total, int BackedUp, int Unchanged, int Failed, int NoBackup);

/// <summary>
/// 자산 상태 통합 서비스.
/// DEXA 자산/에이전트/백업 데이터 + TWM 태그/그룹/ping 병합.
/// </summary>
public class AssetStatusService
{
    /// <summary>
    /// KPI 집계 단일 정의 — Nav/Assets/Dashboard API 와 Blazor(NavMenu/Home)가 모두 이걸 사용.
    /// 정의를 바꾸면 전 화면이 함께 바뀐다(과거엔 5곳에 복붙돼 불일치 위험).
    /// </summary>
    public static KpiSummary ComputeKpi(IReadOnlyCollection<AssetStatusInfo> statuses)
    {
        var total = statuses.Count;
        var backupSuccess = statuses.Count(s => s.LastBackupSucceeded);
        var changed = statuses.Count(s => s.LastBackupChanged == true);
        var noBackup = statuses.Count(s => s.LastBackupTime == null);
        return new KpiSummary(
            Total: total,
            BackedUp: changed,
            Unchanged: backupSuccess - changed,
            Failed: total - backupSuccess - noBackup,
            NoBackup: noBackup);
    }

    private readonly AssetService _assetService;
    private readonly DexaReadService _dexaRead;
    private readonly PingDbService _pingDb;
    private readonly LayoutDbService _layoutDb;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AssetStatusService> _logger;

    // 합성 결과(자산+에이전트+액션+핑+라인맵 병합) 캐시.
    // 이 서비스는 Scoped → 첫 로딩에 /api/nav 와 /api/dashboard 가 별도 요청(별도 인스턴스)으로
    // 동시에 GetAssetStatusesAsync 를 호출한다. 합성 자체가 무캐시 GetAllActionsAsync(전체 스캔)를
    // 포함하므로 매 호출이 비싸다. 싱글톤 IMemoryCache + static 세마포어로 (a)중복 병합과
    // (b)콜드스타트 동시 2회 DB 조회를 single-flight 로 합친다. TTL 은 하위 액션 캐시(15s)와 동일.
    private const string CacheKey = "asset_statuses_composed";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public AssetStatusService(AssetService assetService, DexaReadService dexaRead, PingDbService pingDb, LayoutDbService layoutDb, IMemoryCache cache, ILogger<AssetStatusService> logger)
    {
        _assetService = assetService;
        _dexaRead = dexaRead;
        _pingDb = pingDb;
        _layoutDb = layoutDb;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>전체 자산 상태 목록 (DEXA + TWM 통합). 15초 캐시 + single-flight 로 중복 병합 방지.</summary>
    public async Task<List<AssetStatusInfo>> GetAssetStatusesAsync()
    {
        if (_cache.TryGetValue(CacheKey, out List<AssetStatusInfo>? cached))
            return cached!;

        await BuildLock.WaitAsync();
        try
        {
            // 락 대기 중 다른 호출이 채웠으면 그대로 사용 (콜드스타트 동시 호출 합치기)
            if (_cache.TryGetValue(CacheKey, out cached))
                return cached!;

            var built = await BuildAssetStatusesAsync();
            // 빈 결과(조회 실패)는 캐시하지 않음 — 다음 호출이 재시도하도록.
            if (built.Count > 0)
                _cache.Set(CacheKey, built, CacheTtl);
            return built;
        }
        finally
        {
            BuildLock.Release();
        }
    }

    private async Task<List<AssetStatusInfo>> BuildAssetStatusesAsync()
    {
        try
        {
            // 독립적인 쿼리를 병렬 실행 (자산+aug+conn 병합은 AssetService에 위임)
            var assetsTask     = _assetService.GetMergedAssetsAsync();
            var agentsTask     = _dexaRead.GetAgentsAsync();
            var allActionsTask = _dexaRead.GetAllActionsAsync();
            var pingTask       = _pingDb.GetAllPingResultsAsync();
            var lineTask       = _layoutDb.GetTwmsLayoutLineMapAsync();
            await Task.WhenAll(assetsTask, agentsTask, allActionsTask, pingTask, lineTask);

            var assets      = assetsTask.Result;
            var agents      = agentsTask.Result;
            var pingResults = pingTask.Result;
            var lineMap     = lineTask.Result;

            var agentMap = agents
                .GroupBy(a => a.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var pingMap  = pingResults.ToDictionary(p => p.DexaAssetId);

            // 자산별 최신 액션 + 연속 실패 횟수 — 전체 액션에서 함께 파생
            // (별도 GetLatestActionPerAssetAsync 쿼리 제거: 최신 = 자산별 MAX(actionId) 와 동일)
            var allActions = allActionsTask.Result;
            var byAsset = allActions.GroupBy(a => a.AssetId).ToList();
            var latestActionMap = byAsset
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Id).First());

            // 자산별 연속 실패 횟수 (최신부터 역순, 성공 만나면 중단)
            var consecutiveFailMap = byAsset
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(a => a.Id)
                          .TakeWhile(a => !a.IsSuccess)
                          .Count());

            var result = new List<AssetStatusInfo>();

            foreach (var asset in assets.Where(a => a.IsRealAsset))
            {
                var preferredAgents = asset.AssetAgentPreferences?
                    .Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];

                var agentOnline = preferredAgents.Any(name =>
                    agentMap.TryGetValue(name.Trim(), out var ag) && ag.Online);

                var agentName = preferredAgents.FirstOrDefault()?.Trim();

                latestActionMap.TryGetValue(asset.AssetId, out var lastAction);
                pingMap.TryGetValue(asset.AssetId, out var ping);

                var lastBackupSucceeded = lastAction?.Finished != null && lastAction.IsSuccess;
                var lastBackupTime = lastAction?.Finished ?? lastAction?.Started;
                var lastBackupChanged = lastAction?.ContentsChanged;
                var lastBackupInProgress = lastAction?.IsInProgress == true;

                var info = new AssetStatusInfo
                {
                    AssetId              = asset.AssetId,
                    Name                 = asset.DisplayName,
                    Ip                   = asset.Ip,
                    AssetTypeName        = asset.AssetTypeUserFriendlyName,
                    AssetTypeId          = asset.AssetTypeId,
                    AgentOnline          = agentOnline,
                    AgentName            = agentName,
                    LastBackupTime       = lastBackupTime,
                    LastBackupSucceeded  = lastBackupSucceeded,
                    LastBackupChanged    = lastBackupChanged,
                    LastBackupInProgress = lastBackupInProgress,
                    GroupName            = null,
                    LatestPing           = ping != null ? new PingStatus
                    {
                        Reachable   = ping.Reachable,
                        RoundtripMs = ping.RoundtripMs,
                        CheckedAt   = ping.CheckedAt,
                    } : null,
                    AugIpVia         = asset.AugIpVia,
                    AugLineId        = asset.AugLineId,
                    AugIsRobotPLC    = asset.AugIsRobotPLC,
                    Description      = asset.Description,
                    ModelVersion     = asset.ModelVersion,
                    AugStationNumber = asset.AugStationNumber,
                    AugVendor        = asset.AugVendor,
                    AugSpec          = asset.AugSpec,
                    AugBaseNumber    = asset.AugBaseNumber,
                    AugSlotNumber    = asset.AugSlotNumber,
                    LayoutLineName = asset.AugLineId.HasValue && lineMap.TryGetValue(asset.AugLineId.Value, out var lineName)
                                     ? lineName : null,
                };

                info.Health = ComputeHealth(info);
                consecutiveFailMap.TryGetValue(asset.AssetId, out var failCount);
                info.ConsecutiveFailureCount = failCount;
                result.Add(info);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "자산 상태 조회 실패");
            return [];
        }
    }

    /// <summary>대시보드 전체 데이터 (statuses에서 PingSummary 직접 집계, 이중 조회 방지)</summary>
    public async Task<DashboardData> GetDashboardDataAsync()
    {
        var statuses = await GetAssetStatusesAsync();
        var withPing = statuses.Where(s => s.LatestPing != null).ToList();

        return new DashboardData
        {
            Assets = statuses,
            Ping = new PingSummary
            {
                ReachableCount = withPing.Count(s => s.LatestPing!.Reachable),
                UnreachableCount = withPing.Count(s => !s.LatestPing!.Reachable),
                UnknownCount = statuses.Count - withPing.Count,
            },
        };
    }

    private static AssetHealthStatus ComputeHealth(AssetStatusInfo info)
    {
        if (info.LastBackupTime == null)
            return AssetHealthStatus.Unknown;

        if (info.LastBackupInProgress)
        {
            // IncompleteThreshold 이상 경과: 미완료 → 작업 실패에 포함
            if (info.LastBackupTime.HasValue && (DateTime.Now - info.LastBackupTime.Value) >= DexaAction.IncompleteThreshold)
                return AssetHealthStatus.Failed;
            return AssetHealthStatus.InProgress;
        }

        if (!info.LastBackupSucceeded)
            return AssetHealthStatus.Failed;

        if (info.LastBackupChanged == true)
            return AssetHealthStatus.BackedUp;

        return AssetHealthStatus.Unchanged;
    }
}
