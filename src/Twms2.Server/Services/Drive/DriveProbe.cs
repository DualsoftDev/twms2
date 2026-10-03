namespace Twms2.Server.Services.Drive;

/// <summary>
/// 조회 대상 1건의 연결 정보. 경유(ViaIp)를 쓰면 경유 PLC 로 붙고 Ip 는 그 너머의 드라이브 주소다.
/// DEXA 드라이브 파라미터의 Destination.IP / 1 depth.connection / base number / slot number 와 1:1 이다.
/// </summary>
public sealed record DriveTarget(
    string Ip,
    int Port = 502,
    string? ViaIp = null,
    int ViaPort = XgtTunnelClient.DefaultPort,
    int ViaBase = 0,
    int ViaSlot = 0,
    int TimeoutMs = 1500)
{
    public bool ViaUse => !string.IsNullOrWhiteSpace(ViaIp);
}

/// <summary>드라이브에서 읽은 식별 정보.</summary>
public sealed class DriveIdentity
{
    /// <summary>기종 코드(0x0300). 프로파일 표에 없으면 Series 가 "Unknown(코드)" 가 된다.</summary>
    public ushort ModelCode { get; init; }
    public string Series { get; init; } = "";

    /// <summary>모델버전(0x0E0B ÷ 100). DriveView 9 등록정보의 "버전" 이자 .INV 파일명 — DEXA modelVersion 이 이 값이다.</summary>
    public string? ModelVersion { get; init; }

    /// <summary>Inverter SW Version(0x0303). 펌웨어 버전이며 모델버전과는 별개 값이다.</summary>
    public string? InvSwVersion { get; init; }

    public IReadOnlyList<(ushort Addr, ushort Value)> RawRegisters { get; init; } = Array.Empty<(ushort, ushort)>();
}

/// <summary>접속 방식(직접 / 경유)에 상관없이 레지스터를 읽어주는 통로.</summary>
public interface IRegisterReader : IAsyncDisposable
{
    Task<ushort[]> ReadAsync(byte unitId, byte fc, ushort addr, ushort count, int timeoutMs, CancellationToken ct);
}

/// <summary>드라이브에 직접 Modbus-TCP (1단 연결).</summary>
public sealed class DirectReader : IRegisterReader
{
    private readonly ModbusTcpClient _cli = new();

    public static async Task<DirectReader> OpenAsync(DriveTarget t, CancellationToken ct)
    {
        var r = new DirectReader();
        await r._cli.ConnectAsync(t.Ip, t.Port, t.TimeoutMs, ct).ConfigureAwait(false);
        return r;
    }

    public Task<ushort[]> ReadAsync(byte unitId, byte fc, ushort addr, ushort count, int timeoutMs, CancellationToken ct)
        => _cli.ReadRegistersAsync(unitId, fc, addr, count, timeoutMs, ct);

    public ValueTask DisposeAsync() => _cli.DisposeAsync();
}

/// <summary>XGT PLC 경유 (2단 연결: 경유 IP + Base + Slot).</summary>
public sealed class ViaReader : IRegisterReader
{
    private readonly XgtTunnelClient _cli = new();

    public static async Task<ViaReader> OpenAsync(DriveTarget t, CancellationToken ct)
    {
        if (!t.ViaUse) throw new ArgumentException("경유 IP 가 비어 있습니다.");
        var r = new ViaReader();
        await r._cli.ConnectAsync(t.ViaIp!, t.ViaPort, t.TimeoutMs, ct).ConfigureAwait(false);
        await r._cli.OpenAsync(t.ViaBase, t.ViaSlot, t.Ip, t.Port, t.TimeoutMs, t.TimeoutMs, ct).ConfigureAwait(false);
        return r;
    }

    public Task<ushort[]> ReadAsync(byte unitId, byte fc, ushort addr, ushort count, int timeoutMs, CancellationToken ct)
        => _cli.ReadRegistersAsync(unitId, fc, addr, count, timeoutMs, ct);

    public ValueTask DisposeAsync() => _cli.DisposeAsync();
}

/// <summary>
/// DriveView 9 가 접속 직후에 하는 것과 같은 순서로 식별 레지스터만 읽는다.
/// 기종코드가 안 읽히면 전체 실패, 버전들은 못 읽어도 나머지를 돌려준다.
/// 읽기 전용(FC03) 세 번이라 가동 중인 설비에 영향이 없다.
/// </summary>
public static class DriveProbe
{
    public static async Task<DriveIdentity> IdentifyAsync(DriveTarget target, DriveProfile profile, CancellationToken ct)
    {
        await using IRegisterReader reader = target.ViaUse
            ? await ViaReader.OpenAsync(target, ct).ConfigureAwait(false)
            : await DirectReader.OpenAsync(target, ct).ConfigureAwait(false);

        byte unit = profile.Read.UnitId, fc = profile.Read.FunctionCode;
        int timeoutMs = target.TimeoutMs;
        var raw = new List<(ushort, ushort)>();

        async Task<ushort> One(ushort addr)
        {
            var v = await reader.ReadAsync(unit, fc, addr, 1, timeoutMs, ct).ConfigureAwait(false);
            raw.Add((addr, v[0]));
            return v[0];
        }
        async Task<ushort?> OneSoft(ushort? addr)
        {
            if (addr is not ushort a) return null;
            try { return await One(a).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        }

        // 1) 기종코드 — 이건 실패하면 전체 실패
        ushort modelCode = await One(profile.Read.ModelAddr).ConfigureAwait(false);

        // 2) 모델버전 — 백업 파라미터 맵(.INV)을 고르는 값
        var mv = await OneSoft(profile.ModelVersionAddrFor(modelCode)).ConfigureAwait(false);

        // 3) Inverter SW Version — 참고용 펌웨어 버전
        var sw = await OneSoft(profile.Read.InvSwVersionAddr).ConfigureAwait(false);

        string? modelVersion = mv is ushort m ? DriveProfile.FormatHundredths(m) : null;

        // 코드 15 는 H100 과 초기 H100Plus(602.00)가 공유한다. 602.01 부터는 20 이라 코드로 갈리지만
        // 602.00 만은 버전으로 가를 수밖에 없다 — DataFile 헤더 대조로 확인된 규칙이다.
        string series = profile.SeriesName(modelCode);
        if (modelCode == 15 && mv is ushort v15 && v15 >= 60000)
            series = "H100Plus";

        return new DriveIdentity
        {
            ModelCode = modelCode,
            Series = series,
            ModelVersion = modelVersion,
            InvSwVersion = sw is ushort s ? DriveProfile.FormatByteVersion(s) : null,
            RawRegisters = raw,
        };
    }
}
