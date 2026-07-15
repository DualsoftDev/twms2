using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Helpers;
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
    public async Task<IActionResult> Get([FromQuery] int failThreshold = 3)
    {
        // 주의 필요 자산 기준(연속 실패 횟수) — 사용자가 우측 상단 메뉴로 설정. 기본 3회.
        var threshold = Math.Clamp(failThreshold, 1, 100);

        // 최근 작업은 "오늘 하루" 전체를 최신순으로 (페이지네이션은 클라이언트)
        var coreTask = _dash.GetDashboardCoreAsync(todayActivities: true);
        var statusTask = _status.GetAssetStatusesAsync();
        await Task.WhenAll(coreTask, statusTask);
        var core = coreTask.Result;
        var statuses = statusTask.Result;

        // KPI (단일 정의)
        var kpi = AssetStatusService.ComputeKpi(statuses);
        double Pct(int v) => kpi.Total > 0 ? Math.Round(100.0 * v / kpi.Total, 1) : 0;

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

        // 주의 필요 (연속 N회 이상 실패) — 임계값은 사용자 설정, 전체 반환 후 클라이언트가 페이지네이션
        var attention = statuses
            .Where(a => a.ConsecutiveFailureCount >= threshold)
            .OrderByDescending(a => a.ConsecutiveFailureCount)
            .Select(a => new
            {
                assetId = a.AssetId,
                name = a.Name,
                typeName = a.AssetTypeName,
                health = LayoutHelpers.GetHealthKey(a.Health),
                healthLabel = Helpers.LayoutHelpers.GetHealthLabel(a.Health),
                consecutiveFailureCount = a.ConsecutiveFailureCount,
            })
            .ToList();

        // 오늘 하루 전체 활동(최신순) — 클라이언트가 다음/이전으로 페이지네이션
        var activities = core.RecentActivities
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
                total = kpi.Total,
                backedUp = kpi.BackedUp, backedUpPct = Pct(kpi.BackedUp),
                unchanged = kpi.Unchanged, unchangedPct = Pct(kpi.Unchanged),
                failed = kpi.Failed, failedPct = Pct(kpi.Failed),
            },
            typeStats,
            lineStats,
            attention,
            attentionThreshold = threshold,
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

}
