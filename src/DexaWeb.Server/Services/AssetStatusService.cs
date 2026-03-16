using DexaWeb.Server.Models.Dashboard;
using DexaWeb.Server.Models.Dexa;

namespace DexaWeb.Server.Services;

/// <summary>
/// 자산 상태 통합 서비스.
/// DEXA 자산/에이전트/백업 데이터 + TWM 태그/그룹/ping 병합.
/// </summary>
public class AssetStatusService
{
    private readonly AssetService _assetService;
    private readonly DexaReadService _dexaRead;
    private readonly TwmDbService _twmDb;
    private readonly ILogger<AssetStatusService> _logger;

    public AssetStatusService(AssetService assetService, DexaReadService dexaRead, TwmDbService twmDb, ILogger<AssetStatusService> logger)
    {
        _assetService = assetService;
        _dexaRead = dexaRead;
        _twmDb = twmDb;
        _logger = logger;
    }

    /// <summary>전체 자산 상태 목록 (DEXA + TWM 통합)</summary>
    public async Task<List<AssetStatusInfo>> GetAssetStatusesAsync()
    {
        try
        {
            // 독립적인 쿼리를 병렬 실행 (자산+aug+conn 병합은 AssetService에 위임)
            var assetsTask     = _assetService.GetMergedAssetsAsync();
            var agentsTask     = _dexaRead.GetAgentsAsync();
            var actionsTask    = _dexaRead.GetLatestActionPerAssetAsync();
            var allActionsTask = _dexaRead.GetAllActionsAsync();
            var pingTask       = _twmDb.GetAllPingResultsAsync();
            var lineTask       = _twmDb.GetTwmsLayoutLineMapAsync();
            await Task.WhenAll(assetsTask, agentsTask, actionsTask, allActionsTask, pingTask, lineTask);

            var assets      = assetsTask.Result;
            var agents      = agentsTask.Result;
            var actions     = actionsTask.Result;
            var pingResults = pingTask.Result;
            var lineMap     = lineTask.Result;

            var agentMap = agents.ToDictionary(a => a.Name ?? "", a => a, StringComparer.OrdinalIgnoreCase);
            var pingMap  = pingResults.ToDictionary(p => p.DexaAssetId);

            // 자산별 최신 액션 (assetId → 최신 action)
            var latestActionMap = actions
                .GroupBy(a => a.AssetId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Started).First());

            // 자산별 연속 실패 횟수 (최신부터 역순, 성공 만나면 중단)
            var allActions = allActionsTask.Result;
            var consecutiveFailMap = allActions
                .GroupBy(a => a.AssetId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(a => a.Id)
                          .TakeWhile(a => a.Memo?.ToLower() != "true")
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
