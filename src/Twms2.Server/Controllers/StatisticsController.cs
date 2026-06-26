using Microsoft.AspNetCore.Mvc;
using Twms2.Server.Services;

namespace Twms2.Server.Controllers;

/// <summary>
/// 통계(Statistics) 정적 페이지용 집계 API. 기간 내 백업 액션(추이/성공·실패·변화)과
/// 온·오프라인 전환(TwmsPingLog) 을 <b>자산×일자</b> 버킷으로 최소 집계해 내려준다.
/// 전체/라인별/타입별/개별 분해와 비율·추이 계산은 클라이언트가 이 버킷을 합산해 수행(필터 변경 시 재조회 없음).
/// 기존 DexaReadService / PingDbService / AssetStatusService 를 얇게 래핑(신규 비즈니스 로직 없음).
/// </summary>
[ApiController]
[Route("api/statistics")]
// 공개 읽기(모니터링/조회 페이지). 쓰기 없음 — 대시보드/통합조회와 동일 정책.
public class StatisticsController : ControllerBase
{
    private readonly DexaReadService _dexaRead;
    private readonly PingDbService _pingDb;
    private readonly AssetStatusService _status;

    // 페이로드/조회 비용 상한 — 사용자 지정 범위가 과도해도 안전하게 자른다.
    private const int MaxDays = 366;

    public StatisticsController(DexaReadService dexaRead, PingDbService pingDb, AssetStatusService status)
    {
        _dexaRead = dexaRead;
        _pingDb = pingDb;
        _status = status;
    }

    /// <summary>
    /// 기간 통계 스냅샷. ?start=&end= (yyyy-MM-dd, end 포함). 미지정 시 최근 7일.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? start, [FromQuery] string? end)
    {
        // ── 기간 정규화 ──
        var today = DateTime.Today;
        var endDate = DateTime.TryParse(end, out var e) ? e.Date : today;
        var startDate = DateTime.TryParse(start, out var s) ? s.Date : endDate.AddDays(-6);
        if (startDate > endDate) (startDate, endDate) = (endDate, startDate);
        // 최대 일수 제한 (end 기준으로 자른다)
        if ((endDate - startDate).Days + 1 > MaxDays)
            startDate = endDate.AddDays(-(MaxDays - 1));

        var dayCount = (endDate - startDate).Days + 1;
        var days = Enumerable.Range(0, dayCount)
            .Select(i => startDate.AddDays(i).ToString("yyyy-MM-dd"))
            .ToList();
        int DayIndex(DateTime dt) => (dt.Date - startDate).Days;

        // ── 원천 데이터 동시 조회 ──
        // 액션은 [startDate, endDate+1) (GetActionsInRangeAsync 의 to 는 배타적), 핑로그는 end 포함(서비스가 +1일 처리).
        var actionsTask = _dexaRead.GetActionsInRangeAsync(startDate, endDate.AddDays(1));
        var pingsTask = _pingDb.GetPingLogsAsync(startDate, endDate);
        var statusTask = _status.GetAssetStatusesAsync();
        await Task.WhenAll(actionsTask, pingsTask, statusTask);

        var actions = actionsTask.Result;
        var pings = pingsTask.Result;
        var statuses = statusTask.Result;

        // ── 자산 메타 (그룹핑용): id → 표시명/라인/타입(대시보드와 동일하게 Robot PLC 분리) ──
        var meta = statuses.Select(a => new
        {
            id = a.AssetId,
            name = a.Name,
            line = a.LayoutLineName ?? "라인없음",
            type = a.AugIsRobotPLC == 1 ? "Robot PLC" : (a.AssetTypeName ?? "기타"),
        }).ToList();

        var assets = meta.OrderBy(a => a.name).Select(a => new { a.id, a.name, a.line, a.type }).ToList();
        // "라인없음" 은 항상 맨 뒤로 (정렬 키를 최대 문자로 치환)
        var lines = meta.Select(a => a.line).Distinct().OrderBy(l => l == "라인없음" ? "￿" : l).ToList();
        var types = meta.Select(a => a.type).Distinct().OrderBy(t => t).ToList();

        // ── 백업 액션 → (자산,일자) 버킷 ──
        // n=시도수, c=변화(성공+내용변경), u=변경없음(성공+무변경), f=실패(미완료 포함), p=작업중.
        //   성공 s = c + u, 시도 n = c + u + f + p (불변식).
        var backup = new Dictionary<(int asset, int day), int[]>(); // [n,c,u,f,p]
        foreach (var a in actions)
        {
            if (!a.Started.HasValue) continue;
            var di = DayIndex(a.Started.Value);
            if (di < 0 || di >= dayCount) continue;

            var key = (a.AssetId, di);
            if (!backup.TryGetValue(key, out var cell)) backup[key] = cell = new int[5];

            cell[0]++; // n
            // 판정 순서는 HistoryController.GetResultKey 와 동일하게 작업중 → 실패 → 변화 → 변경없음.
            // (IsSuccess 와 IsInProgress 는 상호배타가 아님 — 성공 카운트가 찍힌 미종료 행은 양쪽 페이지에서
            //  동일하게 '작업중'으로 분류되어야 한다.)
            if (a.IsInProgress && !a.IsIncomplete) cell[4]++;  // p (작업중)
            else if (!a.IsSuccess) cell[3]++;                  // f (실패/미완료)
            else if (a.ContentsChanged == true) cell[1]++;     // c (변화)
            else cell[2]++;                                    // u (변경없음)
        }

        var backupBuckets = backup
            .Select(kv => new
            {
                a = kv.Key.asset, d = kv.Key.day,
                n = kv.Value[0], c = kv.Value[1], u = kv.Value[2], f = kv.Value[3], p = kv.Value[4],
            })
            .ToList();

        // ── 핑 전환 → (자산,일자) 버킷 ── TwmsPingLog 는 상태 변경 시에만 적재되므로 각 행이 곧 전환 이벤트.
        var trans = new Dictionary<(int asset, int day), int[]>(); // [online,offline]
        foreach (var p in pings)
        {
            var di = DayIndex(p.CheckedAt);
            if (di < 0 || di >= dayCount) continue;
            var key = (p.DexaAssetId, di);
            if (!trans.TryGetValue(key, out var cell)) trans[key] = cell = new int[2];
            if (p.Reachable) cell[0]++; else cell[1]++;
        }

        var transitionBuckets = trans
            .Select(kv => new { a = kv.Key.asset, d = kv.Key.day, on = kv.Value[0], off = kv.Value[1] })
            .ToList();

        return Ok(new
        {
            start = startDate.ToString("yyyy-MM-dd"),
            end = endDate.ToString("yyyy-MM-dd"),
            days,
            assets,
            lines,
            types,
            backup = backupBuckets,
            transition = transitionBuckets,
        });
    }
}
