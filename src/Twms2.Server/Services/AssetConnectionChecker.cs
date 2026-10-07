using System.Diagnostics;
using Twms2.Server.HOCON;
using Twms2.Server.Services.Drive;

namespace Twms2.Server.Services;

/// <summary>등록 폼의 연결 정보 그대로. 경유(ViaIp)가 있으면 2단 연결이다.</summary>
public sealed record ConnectionCheckRequest(
    int AssetTypeId,
    string Ip,
    string? ViaIp = null,
    int ViaBase = 0,
    int? ViaSlot = null);

/// <summary>드라이브에서 읽은 식별값과 DEXA·DriveView 기준의 판정.</summary>
/// <param name="ModelName">DEXA 템플릿 후보 중 일치하는 이름. 없으면 null (등록 불가 기종).</param>
/// <param name="InCatalog">DriveView 9 DataFile 에 그 버전 정의가 있는지. 서버에 DriveView 9 가 없으면 null.</param>
public sealed record DriveIdentityResult(
    string Series,
    int ModelCode,
    string? ModelName,
    string? ModelVersion,
    string? InvSwVersion,
    bool DexaSupported,
    bool? InCatalog);

/// <param name="Method">icmp · deep-ping · modbus-tcp · xgt-tunnel — 어떤 경로로 확인했는지.</param>
public sealed record ConnectionCheckResult(
    bool Reachable,
    int? RttMs,
    string Method,
    DriveIdentityResult? Drive,
    string? Error);

/// <summary>
/// 자산 등록 전 "연결 확인". 타입에 따라 하는 일이 다르다.
///   HMI/PLC/서보 : 핑 — 경유가 있으면 DeepPing(XGT pass-through), 없으면 ICMP. 기존 핑 서비스를 그대로 쓴다.
///   드라이브     : 실제로 Modbus 로 접속해 기종코드·모델버전을 읽는다. 접속 자체가 연결 확인이고,
///                  읽은 값으로 폼의 모델명·버전을 채운다 — 템플릿 기본값(iS7 1.00)이 조용히 남아
///                  백업이 어긋나는 일을 막는 게 목적이다.
///                  단, 실험 기능(Features:DriveIdentify)이 꺼져 있으면 드라이브도 다른 타입처럼 핑만 한다 —
///                  식별 레지스터 읽기는 현장 세팅 때만 쓰는 기능이라 평소에는 서버 차원에서 닫아 둔다.
/// DEXA 서버 연결은 필요 없다. 순수 네트워크 작업이다.
/// </summary>
public sealed class AssetConnectionChecker
{
    /// <summary>DEXA 2.20 "LS Drive" 템플릿의 modelName candidates. 템플릿을 못 읽을 때만 쓴다.</summary>
    private static readonly string[] FallbackDexaModels = ["S100", "H100", "G100", "iS7", "S300"];

    private readonly PingService _ping;
    private readonly DexaReadService _dexaRead;
    private readonly AppSettingsEditor _settings;
    private readonly ILogger<AssetConnectionChecker> _logger;

    public AssetConnectionChecker(PingService ping, DexaReadService dexaRead, AppSettingsEditor settings, ILogger<AssetConnectionChecker> logger)
    {
        _ping = ping;
        _dexaRead = dexaRead;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ConnectionCheckResult> CheckAsync(ConnectionCheckRequest req, CancellationToken ct)
    {
        // 버튼을 숨기는 것만으로는 부족하다 — API 가 열려 있으면 그대로 읽히므로 여기서 닫는다.
        if (req.AssetTypeId == AssetService.DriveTypeId && _settings.DriveIdentify)
            return await CheckDriveAsync(req, ct).ConfigureAwait(false);

        bool via = !string.IsNullOrWhiteSpace(req.ViaIp);
        var sw = Stopwatch.StartNew();
        var st = via
            ? await _ping.PingViaDllAsync(req.ViaIp!, req.ViaBase, req.ViaSlot, req.Ip).ConfigureAwait(false)
            : await _ping.PingHostAsync(req.Ip).ConfigureAwait(false);
        sw.Stop();

        return new ConnectionCheckResult(
            st.Reachable,
            st.RoundtripMs ?? (st.Reachable ? (int)sw.ElapsedMilliseconds : null),
            via ? "deep-ping" : "icmp",
            null,
            st.Reachable ? null : (via ? "경유 PLC 를 통해 장비에 닿지 못했습니다." : "핑 응답이 없습니다."));
    }

    private async Task<ConnectionCheckResult> CheckDriveAsync(ConnectionCheckRequest req, CancellationToken ct)
    {
        var target = new DriveTarget(req.Ip, ViaIp: req.ViaIp, ViaBase: req.ViaBase, ViaSlot: req.ViaSlot ?? 0);
        string method = target.ViaUse ? "xgt-tunnel" : "modbus-tcp";
        var profile = DriveProfile.Load(_logger);

        // 한 번만 재시도한다. 같은 경유 모듈로 DEXA 백업이 도는 순간이면 터널 핸들을 못 받을 수 있는데,
        // 그건 몇 초면 풀린다. 더 돌면 사용자가 버튼을 다시 누르는 편이 낫다.
        Exception? last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(700, ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            try
            {
                var id = await DriveProbe.IdentifyAsync(target, profile, ct).ConfigureAwait(false);
                sw.Stop();

                var candidates = await DexaModelCandidatesAsync().ConfigureAwait(false);
                var modelName = candidates.FirstOrDefault(c => c.Equals(id.Series, StringComparison.OrdinalIgnoreCase));
                bool? inCatalog = modelName is not null && id.ModelVersion is not null
                    ? DriveCatalog.HasVersion(modelName, id.ModelVersion)
                    : null;

                _logger.LogInformation(
                    "드라이브 식별 {Ip} via={Via} → {Series}(code {Code}) ver={Ver} sw={Sw} {Ms}ms",
                    req.Ip, req.ViaIp ?? "-", id.Series, id.ModelCode, id.ModelVersion, id.InvSwVersion, sw.ElapsedMilliseconds);

                return new ConnectionCheckResult(
                    true, (int)sw.ElapsedMilliseconds, method,
                    new DriveIdentityResult(id.Series, id.ModelCode, modelName, id.ModelVersion, id.InvSwVersion,
                        modelName is not null, inCatalog),
                    null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogDebug(ex, "드라이브 식별 시도 {Attempt} 실패: {Ip} via={Via}", attempt + 1, req.Ip, req.ViaIp ?? "-");
            }
        }

        return new ConnectionCheckResult(false, null, method, null, last?.Message ?? "접속하지 못했습니다.");
    }

    /// <summary>
    /// DEXA 가 받아주는 드라이브 기종 — 타입 템플릿의 modelName candidates 를 그대로 읽는다.
    /// 코드에 박아두면 DEXA 가 기종을 추가해도 TWMS 가 거부하게 되므로 템플릿을 우선한다.
    /// </summary>
    private async Task<string[]> DexaModelCandidatesAsync()
    {
        try
        {
            var types = await _dexaRead.GetAssetTypesAsync().ConfigureAwait(false);
            var drive = types.FirstOrDefault(t => t.Id == AssetService.DriveTypeId);
            if (!string.IsNullOrEmpty(drive?.Parameter))
            {
                var item = new Parameter(drive.Parameter).Items
                    .FirstOrDefault(it => it.Key.Equals("modelName", StringComparison.OrdinalIgnoreCase));
                var c = item?.Candidates.Where(s => s.Length > 0).ToArray();
                if (c is { Length: > 0 }) return c;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "드라이브 타입 템플릿을 읽지 못해 내장 후보 목록을 씁니다");
        }
        return FallbackDexaModels;
    }
}
