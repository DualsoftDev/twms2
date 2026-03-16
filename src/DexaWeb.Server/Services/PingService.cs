using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DexaWeb.Dexa.Models;
using DexaWeb.Server.Models.Dashboard;
using DexaWeb.Server.Models.Dexa;

namespace DexaWeb.Server.Services;

/// <summary>
/// 자산 IP ping 서비스.
/// 1차: IP만 존재(AugIpVia 없음)하는 자산 → ICMP ping.
/// 2차: AugIpVia가 있는 자산 → pingtest.dll 경유 (추후 연동).
/// IP는 DEXA parameter(HOCON)에서 읽음.
/// </summary>
public class PingService
{
    [DllImport("DeepPinger.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int DeepPing(
        string targetIpString, string pingIpString,
        int nBase, int nSlot, int timeoutSec);

    private readonly AssetService _assetService;
    private readonly DexaReadService _dexaRead;
    private readonly TwmDbService _twmDb;
    private readonly ILogger<PingService> _logger;

    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Phase2FailDelay = TimeSpan.FromSeconds(6);
    private const int DeepPingTimeoutSec = 5;

    public PingService(AssetService assetService, DexaReadService dexaRead, TwmDbService twmDb, ILogger<PingService> logger)
    {
        _assetService = assetService;
        _dexaRead = dexaRead;
        _twmDb = twmDb;
        _logger = logger;
    }

    /// <summary>단건 자산 ping (TWM 연결정보 병합, ViaIp 있으면 DeepPing)</summary>
    public async Task<PingStatus?> PingAssetAsync(int dexaAssetId)
    {
        var asset = await _dexaRead.GetViewAssetAsync(dexaAssetId);
        if (asset == null) return null;

        // PLC/Servo는 TwmsAssetConn에 IP/ViaIp가 있으므로 병합 필요
        var connMap = await _twmDb.GetTwmsAssetConnMapAsync();
        if (connMap.TryGetValue(dexaAssetId, out var conn))
            asset.ApplyAug(null, conn);

        if (string.IsNullOrEmpty(asset.Ip)) return null;

        PingStatus result;
        if (!string.IsNullOrEmpty(asset.AugIpVia))
            result = await PingViaDllAsync(asset.AugIpVia, asset.AugBaseNumber ?? 0, asset.AugSlotNumber, asset.Ip);
        else
            result = await PingHostAsync(asset.Ip);

        await _twmDb.UpsertPingResultAsync(dexaAssetId, asset.Ip, result.Reachable, result.RoundtripMs);
        return result;
    }

    /// <summary>캐시된 ping 결과 조회</summary>
    public async Task<Dictionary<int, PingStatus>> GetCachedResultsAsync()
    {
        var results = await _twmDb.GetAllPingResultsAsync();
        return results.ToDictionary(
            r => r.DexaAssetId,
            r => new PingStatus
            {
                Reachable = r.Reachable,
                RoundtripMs = r.RoundtripMs,
                CheckedAt = r.CheckedAt,
            });
    }

    /// <summary>전체 Ping (Phase 1 + Phase 2 순차 실행, 자산 1회만 로드)</summary>
    public async Task PingAllPhasesAsync(
        IProgress<(int completed, int total)>? progress = null,
        CancellationToken ct = default,
        int maxConcurrency = 10)
    {
        var (failedViaIps, mergedAssets) = await PingPhase1Async(ct, maxConcurrency);
        progress?.Report((1, 2)); // Phase 1 완료
        await PingPhase2Async(failedViaIps, mergedAssets, ct);
        progress?.Report((2, 2)); // Phase 2 완료
    }

    // ──────────────── 주기적 핑: 2단계 테스트 ────────────────

    /// <summary>
    /// 1차 테스트: ViaIp가 없는 자산을 ICMP ping.
    /// IP/ViaIp는 DEXA parameter(HOCON) 우선, TwmsAsset 폴백.
    /// 실패한 IP + 병합된 자산 목록을 반환하여 2차에서 재활용.
    /// </summary>
    public async Task<(HashSet<string> FailedIps, List<ViewAsset> MergedAssets)> PingPhase1Async(
        CancellationToken ct = default, int maxConcurrency = 10)
    {
        var mergedAssets = await _assetService.GetMergedAssetsAsync();

        // 1차 대상: 실자산 + IP 있음 + ViaIp 없음 + SkipPing==0
        var targets = mergedAssets
            .Where(a => a.IsRealAsset && !string.IsNullOrEmpty(a.Ip))
            .Where(a => string.IsNullOrEmpty(a.AugIpVia))
            .ToList();

        if (targets.Count == 0) return ([], mergedAssets);

        var failedIps = new ConcurrentBag<string>();
        var semaphore = new SemaphoreSlim(maxConcurrency);
        int completed = 0;

        var tasks = targets.Select(async asset =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await PingHostAsync(asset.Ip!);
                await _twmDb.UpsertPingResultAsync(asset.AssetId, asset.Ip, result.Reachable, result.RoundtripMs);

                if (!result.Reachable)
                    failedIps.Add(asset.Ip!);

                Interlocked.Increment(ref completed);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var failedSet = new HashSet<string>(failedIps, StringComparer.OrdinalIgnoreCase);
        _logger.LogInformation(
            "1차 Ping 완료: {Success}/{Total} 성공, 실패 IP {Failed}건",
            targets.Count - failedSet.Count, targets.Count, failedSet.Count);

        // Gateway PLC IP 별도 ping (AugIpVia로 사용되는 IP들)
        var gatewayIps = mergedAssets
            .Where(a => a.IsRealAsset && !string.IsNullOrEmpty(a.AugIpVia))
            .Select(a => a.AugIpVia!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(ip => !failedSet.Contains(ip)) // 이미 실패 판정된 IP 제외
            .ToList();

        if (gatewayIps.Count > 0)
        {
            _logger.LogInformation("Gateway PLC ping 시작: {Count}건", gatewayIps.Count);
            var failedGateways = new ConcurrentBag<string>();
            var gwTasks = gatewayIps.Select(async gwIp =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var result = await PingHostAsync(gwIp);
                    if (!result.Reachable)
                        failedGateways.Add(gwIp);
                }
                finally
                {
                    semaphore.Release();
                }
            });
            await Task.WhenAll(gwTasks);
            foreach (var gw in failedGateways)
                failedSet.Add(gw);
            _logger.LogInformation("Gateway PLC ping 완료: 실패 {Failed}/{Total}건",
                failedGateways.Count, gatewayIps.Count);
        }

        return (failedSet, mergedAssets);
    }

    /// <summary>
    /// 2차 테스트: ViaIp가 있는 자산을 DeepPinger.dll로 테스트.
    /// - 1차에서 실패한 ViaIp의 하부자산은 즉시 실패 기록 (경유 PLC 오프라인).
    /// - ViaIp별 그룹화 → 그룹 간 동시 실행, 그룹 내 순차 실행.
    /// - 그룹 내 실패 시 6초 대기 후 다음 자산 테스트.
    /// </summary>
    public async Task PingPhase2Async(HashSet<string> failedViaIps, List<ViewAsset> mergedAssets, CancellationToken ct = default)
    {
        var allTargets = mergedAssets
            .Where(a => a.IsRealAsset
                        && !string.IsNullOrEmpty(a.AugIpVia)
                       )
            .ToList();

        var viaIpCount = mergedAssets.Count(a => a.IsRealAsset && !string.IsNullOrEmpty(a.AugIpVia));
        _logger.LogInformation("2차 Ping 진입: ViaIp 설정 자산 {ViaCount}건, SkipPing 제외 후 {TargetCount}건",
            viaIpCount, allTargets.Count);

        if (allTargets.Count == 0)
        {
            _logger.LogInformation("2차 Ping: 대상 없음 (전체 스킵 또는 ViaIp 미설정)");
            return;
        }

        // 경유 PLC 오프라인 → 하부자산 즉시 실패 기록
        var offlineAssets = allTargets.Where(a => failedViaIps.Contains(a.AugIpVia!)).ToList();
        foreach (var asset in offlineAssets)
        {
            if (!string.IsNullOrEmpty(asset.Ip))
                await _twmDb.UpsertPingResultAsync(asset.AssetId, asset.Ip, false, null);
        }
        if (offlineAssets.Count > 0)
            _logger.LogInformation("2차 Ping: 경유 PLC 오프라인으로 {Count}건 즉시 실패 기록", offlineAssets.Count);

        // 경유 PLC 온라인 → 실제 DeepPing 테스트
        var targets = allTargets.Where(a => !failedViaIps.Contains(a.AugIpVia!)).ToList();
        if (targets.Count > 0)
        {
            var groups = targets.GroupBy(a => a.AugIpVia!, StringComparer.OrdinalIgnoreCase);
            var groupTasks = groups.Select(group => PingPhase2GroupAsync(group.Key, group.ToList(), ct));
            await Task.WhenAll(groupTasks);
        }

        _logger.LogInformation("2차 Ping 완료: {Tested}건 테스트, {Offline}건 PLC오프라인",
            targets.Count, offlineAssets.Count);
    }

    /// <summary>
    /// 2차 테스트 그룹 내 순차 실행.
    /// 실패 시 6초 대기 후 다음 자산 테스트.
    /// </summary>
    private async Task PingPhase2GroupAsync(
        string viaIp, List<ViewAsset> assets, CancellationToken ct)
    {
        foreach (var asset in assets)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(asset.Ip)) continue;

            var result = await PingViaDllAsync(viaIp, asset.AugBaseNumber ?? 0, asset.AugSlotNumber, asset.Ip);
            await _twmDb.UpsertPingResultAsync(asset.AssetId, asset.Ip, result.Reachable, result.RoundtripMs);

            if (!result.Reachable)
                await Task.Delay(Phase2FailDelay, ct);
        }
    }

    /// <summary>
    /// DeepPinger.dll 경유 핑 테스트.
    /// 메인 PLC(viaIp)를 경유하여 하부장비(ip)를 XGT Pass-Through로 핑 테스트.
    /// </summary>
    public Task<PingStatus> PingViaDllAsync(string viaIp, int baseNumber, int? slotNumber, string ip)
    {
        return Task.Run(() =>
        {
            try
            {
                int errorCode = DeepPing(viaIp, ip, baseNumber, slotNumber ?? 0, DeepPingTimeoutSec);
                bool reachable = DeepPingError.IsSuccess(errorCode);

                if (!reachable)
                    _logger.LogWarning(
                        "DeepPing 실패: via={Via}, ip={Ip}, base={Base}, slot={Slot}, error={Code}({Category}: {Desc})",
                        viaIp, ip, baseNumber, slotNumber,
                        errorCode, DeepPingError.GetCategory(errorCode), DeepPingError.GetDescription(errorCode));

                return new PingStatus
                {
                    Reachable = reachable,
                    CheckedAt = DateTime.Now,
                };
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogError(ex, "DeepPinger.dll을 찾을 수 없습니다");
                return new PingStatus { Reachable = false, CheckedAt = DateTime.Now };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeepPing 호출 중 예외: via={Via}, ip={Ip}", viaIp, ip);
                return new PingStatus { Reachable = false, CheckedAt = DateTime.Now };
            }
        });
    }

    /// <summary>ICMP ping 실행</summary>
    private async Task<PingStatus> PingHostAsync(string host)
    {
        try
        {
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(host, (int)PingTimeout.TotalMilliseconds);
            return new PingStatus
            {
                Reachable = reply.Status == IPStatus.Success,
                RoundtripMs = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null,
                CheckedAt = DateTime.Now,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ping 실패: {Host}", host);
            return new PingStatus
            {
                Reachable = false,
                CheckedAt = DateTime.Now,
            };
        }
    }
}
