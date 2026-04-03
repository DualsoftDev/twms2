using DexaWeb.Dexa;
using DexaWeb.Server.Data;
using DexaWeb.Server.Models.Dashboard;
using DexaWeb.Server.Models.Dexa;

namespace DexaWeb.Server.Services;

/// <summary>
/// 대시보드 데이터 서비스.
/// DEXA 연결 상태 + 백업 요약 + 에이전트 요약 + 최근 활동.
/// GetDashboardCoreAsync()로 공통 데이터를 1회 조회 후 각 요약에 활용.
/// </summary>
public class DashboardService
{
    private readonly IDexaClient _client;
    private readonly DexaReadService _dexaRead;
    private readonly DexaDbConnection _dexaDbConn;
    private readonly ILogger<DashboardService> _logger;

    public DashboardService(IDexaClient client, DexaReadService dexaRead,
        DexaDbConnection dexaDbConn, ILogger<DashboardService> logger)
    {
        _client = client;
        _dexaRead = dexaRead;
        _dexaDbConn = dexaDbConn;
        _logger = logger;
    }

    /// <summary>DEXA Server 연결 상태</summary>
    public async Task<DexaConnectionStatus> GetConnectionStatusAsync()
    {
        var reachable = await _client.PingServerAsync();
        return new DexaConnectionStatus
        {
            DexaConnected = reachable,
            Timestamp = DateTime.Now,
        };
    }

    /// <summary>
    /// 대시보드 핵심 데이터를 한 번에 조회 (백업 요약 + 최근 활동).
    /// assets/actions를 1회만 조회하여 중복 DB 접근 방지.
    /// </summary>
    public async Task<DashboardCoreResult> GetDashboardCoreAsync(int activityLimit = 20)
    {
        try
        {
            var assetsTask = _dexaRead.GetViewAssetsAsync();
            // 백업 요약: 7일치 전체 조회 (LIMIT 없음)
            var weekActionsTask = _dexaRead.GetActionsInRangeAsync(DateTime.Now.AddDays(-7), DateTime.Now.AddDays(1));
            // 최근 활동: 최신 200건이면 충분 (UI에 20건만 표시)
            var recentActionsTask = _dexaRead.GetRecentActionsAsync(200);
            await Task.WhenAll(assetsTask, weekActionsTask, recentActionsTask);

            var assets = assetsTask.Result;

            var backup = BuildBackupSummary(assets, weekActionsTask.Result);
            var activities = BuildRecentActivities(assets, recentActionsTask.Result, activityLimit);
            var schedule = BuildTodaySchedule(assets, weekActionsTask.Result);
            var drive = GetDexaDbDriveStat();

            return new DashboardCoreResult { Backup = backup, RecentActivities = activities, TodaySchedule = schedule, Drive = drive };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "대시보드 핵심 데이터 조회 실패");
            return new DashboardCoreResult { Backup = new BackupSummary(), RecentActivities = [] };
        }
    }

    /// <summary>에이전트 상태 요약</summary>
    public async Task<AgentSummary> GetAgentSummaryAsync()
    {
        try
        {
            var agents = await _dexaRead.GetAgentsAsync();
            return new AgentSummary
            {
                TotalCount = agents.Count,
                OnlineCount = agents.Count(a => a.Online),
                OfflineCount = agents.Count(a => !a.Online),
                Agents = agents.Select(a => new AgentInfo
                {
                    Id = a.Id,
                    Name = a.Name ?? "",
                    Ip = a.Ip,
                    Online = a.Online,
                    SwVersion = a.SwVersion,
                    Connected = a.Connected,
                    Disconnected = a.Disconnected,
                }).ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "에이전트 요약 조회 실패");
            return new AgentSummary();
        }
    }

    private static BackupSummary BuildBackupSummary(List<ViewAsset> assets, List<DexaAction> actions)
    {
        var realAssetCount = assets.Count(a => a.IsRealAsset);
        var today = DateTime.Today;
        var todayActions = actions.Where(a => a.Started?.Date == today).ToList();
        var week = DateTime.Now.AddDays(-7);
        var weekActions = actions.Where(a => a.Started >= week).ToList();

        return new BackupSummary
        {
            TotalAssets = realAssetCount,
            BackedUpToday = todayActions.Count(a => a.Finished != null),
            FailedToday = todayActions.Count(a => a.Finished == null && a.Started?.AddMinutes(30) < DateTime.Now),
            SuccessRate7d = weekActions.Count > 0
                ? Math.Round(100.0 * weekActions.Count(a => a.Finished != null) / weekActions.Count, 1)
                : 0,
        };
    }

    private DriveStat? GetDexaDbDriveStat()
    {
        try
        {
            var path = _dexaDbConn.DbFilePath;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return new DriveStat
            {
                DriveName = drive.Name,
                DbFilePath = path,
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.AvailableFreeSpace,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "드라이브 용량 조회 실패");
            return null;
        }
    }

    private static List<ScheduleEntry> BuildTodaySchedule(List<ViewAsset> assets, List<DexaAction> weekActions)
    {
        var today = DateTime.Today;
        var assetMap = assets.ToDictionary(a => a.AssetId, a => a.DisplayName);

        return weekActions
            .Where(a => a.Started.HasValue && a.Started.Value.Date == today)
            .Select(a =>
            {
                assetMap.TryGetValue(a.AssetId, out var name);
                return new ScheduleEntry
                {
                    AssetId = a.AssetId,
                    AssetName = name ?? $"Asset #{a.AssetId}",
                    Started = a.Started!.Value,
                    Finished = a.Finished,
                    InProgress = a.IsInProgress && !a.IsIncomplete,
                    Success = a.IsIncomplete ? false : a.IsInProgress ? null : a.IsSuccess,
                };
            })
            .OrderBy(s => s.Started)
            .ToList();
    }

    private static List<RecentActivity> BuildRecentActivities(
        List<ViewAsset> assets, List<DexaAction> actions, int limit)
    {
        var assetMap = assets.ToDictionary(a => a.AssetId, a => a.DisplayName);
        var activities = new List<RecentActivity>();

        foreach (var a in actions)
        {
            assetMap.TryGetValue(a.AssetId, out var name);
            activities.Add(new RecentActivity
            {
                Source = "dexa",
                AssetId = a.AssetId,
                AssetName = name ?? $"Asset #{a.AssetId}",
                Action = a.IsIncomplete ? "작업 미완료" : a.IsInProgress ? "작업 진행중" : "작업",
                Success = a.IsIncomplete ? false : a.IsInProgress ? null : a.IsSuccess,
                Timestamp = a.Started ?? DateTime.MinValue,
            });
        }

        return activities
            .OrderByDescending(a => a.Timestamp)
            .Take(limit)
            .ToList();
    }
}
