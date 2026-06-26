using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Data;
using Twms2.Server.Helpers;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 정적 페이지 공통 셸(shell.js)용 네비게이션 API.
/// 사이드바 자산 트리 + 미니 KPI + 로고를 1회 조회로 제공. NavMenu.razor 로직 이식.
/// </summary>
[ApiController]
[Route("api/nav")]
// 공개 읽기(셸이 모든 공개 페이지에서 호출). isAdmin 은 쿠키가 있으면 Admin 여부로 채움(없으면 false).
public class NavController : ControllerBase
{
    private readonly AssetStatusService _status;
    private readonly DexaReadService _dexa;
    private readonly DexaDbConnection _dexaDb;
    private readonly AppSettingsEditor _settings;

    public NavController(AssetStatusService status, DexaReadService dexa, DexaDbConnection dexaDb, AppSettingsEditor settings)
    {
        _status = status;
        _dexa = dexa;
        _dexaDb = dexaDb;
        _settings = settings;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var statuses = await _status.GetAssetStatusesAsync();
        var dexaOnline = await _dexa.IsConnectedAsync();

        // 관리 메뉴 노출 여부 — 인증 쿠키의 Admin 역할로 판정
        var auth = await HttpContext.AuthenticateAsync(AuthController.Scheme);
        var isAdmin = auth.Succeeded && auth.Principal?.IsInRole("Admin") == true;

        // 미니 KPI (Home/NavMenu 와 동일한 정의)
        var total = statuses.Count;
        var backupSuccess = statuses.Count(s => s.LastBackupSucceeded);
        var changed = statuses.Count(s => s.LastBackupChanged == true);
        var noBackup = statuses.Count(s => s.LastBackupTime == null);
        var failed = total - backupSuccess - noBackup;
        var unchanged = backupSuccess - changed;

        var brand = _settings.GetBrand();

        return Ok(new
        {
            logoUrl = ScanLogoUrl(),
            // 사이드바 브랜드 — 로고 마크 우측 제목/부제(일반설정에서 변경, 저장 즉시 반영).
            navTitle = brand.Title,
            navSubtitle = brand.Subtitle,
            isAdmin,
            dexaOnline,
            kpi = new { total, backedUp = changed, unchanged, failed },
            tree = BuildTree(statuses),
        });
    }

    /// <summary>
    /// DEXA 서버/에이전트 상태 상세 — 셸 헤더 상태 배지 클릭 시 표시.
    /// DB 연결 가능 여부 + 프로바이더/대상 + 등록된 에이전트 목록(온라인 여부 포함).
    /// </summary>
    [HttpGet("dexa-status")]
    public async Task<IActionResult> DexaStatus()
    {
        var online = await _dexa.IsConnectedAsync();
        // 연결된(온라인) 에이전트만 표시
        var agents = online ? (await _dexa.GetAgentsAsync()).Where(a => a.Online).ToList() : [];

        var provider = _dexaDb.Provider == DexaDbProvider.SqlServer ? "MSSQL" : "SQLite";
        // 연결 대상: MSSQL은 DB 스키마명, SQLite는 파일 경로
        var target = _dexaDb.Provider == DexaDbProvider.SqlServer ? _dexaDb.Schema : _dexaDb.DbFilePath;

        return Ok(new
        {
            online,
            provider,
            target,
            agentCount = agents.Count,
            agents = agents
                .OrderBy(a => a.Name)
                .Select(a => new
                {
                    name = a.Name,
                    ip = a.Ip,
                    swVersion = a.SwVersion,
                    connected = a.Connected,
                })
                .ToList(),
        });
    }

    /// <summary>AssetStatusInfo 목록 → 라인/PLC/하위 자산 트리 (NavMenu.BuildAssetTree 이식)</summary>
    private static List<object> BuildTree(List<AssetStatusInfo> assets)
    {
        var result = new List<object>();

        var byLine = assets
            .GroupBy(a => a.LayoutLineName ?? "라인없음")
            .OrderBy(g => g.Key == "라인없음" ? 1 : 0)
            .ThenBy(g => g.Key);

        foreach (var lineGrp in byLine)
        {
            var lineAssets = lineGrp.ToList();

            var ipToAsset = new Dictionary<string, AssetStatusInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in lineAssets)
                if (!string.IsNullOrEmpty(a.Ip)) ipToAsset.TryAdd(a.Ip, a);

            var plcChildren = new Dictionary<int, List<AssetStatusInfo>>();
            var assignedIds = new HashSet<int>();

            foreach (var a in lineAssets)
            {
                if (string.IsNullOrEmpty(a.AugIpVia)) continue;
                if (ipToAsset.TryGetValue(a.AugIpVia, out var plc) && plc.AssetId != a.AssetId)
                {
                    if (!plcChildren.ContainsKey(plc.AssetId)) plcChildren[plc.AssetId] = [];
                    plcChildren[plc.AssetId].Add(a);
                    assignedIds.Add(a.AssetId);
                }
            }

            var plcNodes = new List<object>();
            foreach (var (plcId, children) in plcChildren.OrderBy(kv => kv.Value.First().AugIpVia))
            {
                var plc = lineAssets.First(a => a.AssetId == plcId);
                assignedIds.Add(plc.AssetId);
                var kids = children.OrderBy(c => c.Name).ToList();
                plcNodes.Add(new
                {
                    plc = Node(plc),
                    aggColor = LayoutHelpers.AggregateHealthColor(kids),
                    children = kids.Select(Node).ToList(),
                });
            }

            var standalone = lineAssets
                .Where(a => !assignedIds.Contains(a.AssetId))
                .OrderBy(a => a.Name)
                .Select(Node)
                .ToList();

            result.Add(new
            {
                lineName = lineGrp.Key,
                expanded = false,
                aggColor = LayoutHelpers.AggregateHealthColor(lineAssets),
                // 라인 단위 문제 집계 — 접힌 상태에서도 사이드바에 배지로 노출
                total = lineAssets.Count,
                failed = lineAssets.Count(a => a.Health == AssetHealthStatus.Failed),
                inProgress = lineAssets.Count(a => a.Health == AssetHealthStatus.InProgress),
                offline = lineAssets.Count(a => a.LatestPing is { Reachable: false }),
                plcNodes,
                standalone,
            });
        }

        return result;
    }

    private static object Node(AssetStatusInfo a) => new
    {
        assetId = a.AssetId,
        displayName = a.Name,
        icon = LayoutHelpers.GetAssetIcon(a.AssetTypeName, a.AugIsRobotPLC),
        statusColor = LayoutHelpers.GetHealthColor(a.Health),
        // 상태 키(css/펄스 분기용) + 한글 라벨(툴팁용)
        health = a.Health.ToString().ToLowerInvariant(),  // backedup/unchanged/failed/inprogress/unknown
        healthLabel = LayoutHelpers.GetHealthLabel(a.Health),
        offline = a.LatestPing is { Reachable: false },
    };

    private static string? ScanLogoUrl()
    {
        try
        {
            if (!Directory.Exists(TwmsDataPath.Uploads)) return null;
            var files = Directory.GetFiles(TwmsDataPath.Uploads, "app-logo.*");
            if (files.Length == 0) return null;
            var fi = new FileInfo(files[0]);
            return $"/uploads/{Path.GetFileName(files[0])}?t={fi.LastWriteTimeUtc.Ticks}";
        }
        catch { return null; }
    }
}
