using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 대시보드(Overview) 정적 페이지용 스냅샷 API. Home.razor 의 RefreshAll 집계를 1회 응답으로 제공.
/// 기존 DashboardService / AssetStatusService 를 얇게 래핑(신규 데이터 로직 없음).
/// </summary>
[ApiController]
[Route("api/dashboard")]
// 공개 읽기(대시보드는 첫 화면, 로그인 불필요). 쓰기 없음.
public class DashboardController : ControllerBase
{
    private readonly DashboardService _dash;
    private readonly AssetStatusService _status;

    public DashboardController(DashboardService dash, AssetStatusService status)
    {
        _dash = dash;
        _status = status;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var coreTask = _dash.GetDashboardCoreAsync();
        var statusTask = _status.GetAssetStatusesAsync();
        await Task.WhenAll(coreTask, statusTask);
        var core = coreTask.Result;
        var statuses = statusTask.Result;

        // KPI (Home.RefreshAll 정의)
        var total = statuses.Count;
        var backupSuccess = statuses.Count(s => s.LastBackupSucceeded);
        var changed = statuses.Count(s => s.LastBackupChanged == true);
        var noBackup = statuses.Count(s => s.LastBackupTime == null);
        var failed = total - backupSuccess - noBackup;
        var unchanged = backupSuccess - changed;
        double Pct(int v) => total > 0 ? Math.Round(100.0 * v / total, 1) : 0;

        // 히트맵 정렬 (백업상태 → 온/오프라인 → 이름)
        var heatmap = statuses
            .OrderBy(a => a.Health switch
            {
                AssetHealthStatus.BackedUp => 0,
                AssetHealthStatus.InProgress => 1,
                AssetHealthStatus.Failed => 2,
                AssetHealthStatus.Unknown => 3,
                AssetHealthStatus.Unchanged => 4,
                _ => 5,
            })
            .ThenBy(a => a.LatestPing is { Reachable: false } ? 1 : 0)
            .ThenBy(a => a.Name)
            .Select(a => new
            {
                assetId = a.AssetId,
                name = a.Name,
                health = HealthKey(a.Health),
                offline = a.LatestPing is { Reachable: false },
            })
            .ToList();

        // 타입별 (Robot PLC 분리)
        var typeStats = statuses
            .GroupBy(a => a.AugIsRobotPLC == 1 ? "Robot PLC" : a.AssetTypeName ?? "기타")
            .Select(g => StatGroup(g.Key, g))
            .OrderByDescending(t => t.total)
            .Cast<object>()
            .ToList();

        // 라인별
        var lineStats = statuses
            .GroupBy(a => a.LayoutLineName ?? "라인없음")
            .Select(g => StatGroup(g.Key, g))
            .OrderByDescending(t => t.name == "라인없음" ? -1 : t.total)
            .Cast<object>()
            .ToList();

        // 주의 필요 (연속 3회 이상 실패)
        var attention = statuses
            .Where(a => a.ConsecutiveFailureCount >= 3)
            .OrderByDescending(a => a.ConsecutiveFailureCount)
            .Take(10)
            .Select(a => new
            {
                assetId = a.AssetId,
                name = a.Name,
                typeName = a.AssetTypeName,
                health = HealthKey(a.Health),
                healthLabel = Helpers.LayoutHelpers.GetHealthLabel(a.Health),
                consecutiveFailureCount = a.ConsecutiveFailureCount,
            })
            .ToList();

        var activities = core.RecentActivities
            .Take(8)
            .Select(a => new { assetId = a.AssetId, assetName = a.AssetName, action = a.Action, success = a.Success, timestamp = a.Timestamp })
            .ToList();

        var schedule = core.TodaySchedule
            .Select(e => new { assetId = e.AssetId, assetName = e.AssetName, started = e.Started, finished = e.Finished, inProgress = e.InProgress, success = e.Success })
            .ToList();

        object? drive = core.Drive == null ? null : new
        {
            usedBytes = core.Drive.UsedBytes,
            freeBytes = core.Drive.FreeBytes,
            totalBytes = core.Drive.TotalBytes,
            usedPct = Math.Round(core.Drive.UsedPct, 1),
        };

        return Ok(new
        {
            kpi = new
            {
                total,
                backedUp = changed, backedUpPct = Pct(changed),
                unchanged, unchangedPct = Pct(unchanged),
                failed, failedPct = Pct(failed),
            },
            heatmap,
            typeStats,
            lineStats,
            attention,
            activities,
            schedule,
            drive,
            today = DateTime.Today.ToString("yyyy-MM-dd"),
        });
    }

    private record StatRow(string name, int backedUp, int unchanged, int failed, int inProgress, int unknown)
    {
        public int total => backedUp + unchanged + failed + inProgress + unknown;
    }

    private static StatRow StatGroup(string name, IEnumerable<AssetStatusInfo> g) => new(
        name,
        g.Count(a => a.Health == AssetHealthStatus.BackedUp),
        g.Count(a => a.Health == AssetHealthStatus.Unchanged),
        g.Count(a => a.Health == AssetHealthStatus.Failed),
        g.Count(a => a.Health == AssetHealthStatus.InProgress),
        g.Count(a => a.Health == AssetHealthStatus.Unknown));

    private static string HealthKey(AssetHealthStatus h) => h switch
    {
        AssetHealthStatus.BackedUp => "backedup",
        AssetHealthStatus.Unchanged => "unchanged",
        AssetHealthStatus.Failed => "failed",
        AssetHealthStatus.InProgress => "inprogress",
        _ => "unknown",
    };
}
