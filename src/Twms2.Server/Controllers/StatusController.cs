using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 상태 모니터(StatusMonitor) 정적 페이지용 스냅샷 API.
/// MudDataGrid 가 보여주던 자산/핑 상태 행을 1회 응답으로 제공.
/// AssetStatusService.GetAssetStatusesAsync 를 얇게 래핑(신규 데이터 로직 없음).
/// </summary>
[ApiController]
[Route("api/status")]
// 공개 읽기(상태 모니터). 쓰기 없음.
public class StatusController : ControllerBase
{
    private readonly AssetStatusService _status;

    public StatusController(AssetStatusService status) => _status = status;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var statuses = await _status.GetAssetStatusesAsync();

        var rows = statuses
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                assetId            = a.AssetId,
                name               = a.Name,
                ip                 = a.Ip,
                assetTypeName      = a.AssetTypeName,
                agentOnline        = a.AgentOnline,
                agentName          = a.AgentName,
                lastBackupTime     = a.LastBackupTime,
                lastBackupSucceeded = a.LastBackupSucceeded,
                lastBackupChanged  = a.LastBackupChanged,
                groupName          = a.GroupName,
                health             = HealthKey(a.Health),
                healthLabel        = Helpers.LayoutHelpers.GetHealthLabel(a.Health),
                ping = a.LatestPing == null ? null : new
                {
                    reachable   = a.LatestPing.Reachable,
                    roundtripMs = a.LatestPing.RoundtripMs,
                    checkedAt   = a.LatestPing.CheckedAt,
                },
            })
            .ToList();

        var types = statuses
            .Select(a => a.AssetTypeName)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        return Ok(new { rows, types });
    }

    private static string HealthKey(AssetHealthStatus h) => h switch
    {
        AssetHealthStatus.BackedUp => "backedup",
        AssetHealthStatus.Unchanged => "unchanged",
        AssetHealthStatus.Failed => "failed",
        AssetHealthStatus.InProgress => "inprogress",
        _ => "unknown",
    };
}
