using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Twms2.Server.Services.Drive;

/// <summary>
/// LS XGT 전용 프로토콜(기본 포트 2004) 위로 Modbus-TCP 를 중계하는 경유 접속.
/// PC 는 드라이브에 직접 붙지 않고 경유 PLC 의 2004 포트에 붙어
/// "Base/Slot 모듈이 대상 IP:포트로 Modbus-TCP 를 열어라" 고 지시한다.
/// DriveView 9 가 경유 접속에 쓰는 것과 같은 경로이며, 실제 캡처(via_1.pcapng, iS7 1.04)를
/// 해독해 구현한 E:\DriveScanner 의 XgtTunnelClient 이식.
///
///   XGT 헤더 20바이트
///     +00 "LSIS-XGT"  +08 00×4  +0C CPU Info(0)  +0D Source(PC→PLC 0x33 / PLC→PC 0x11)
///     +0E Invoke ID(LE, 요청마다 증가)  +10 응용데이터 길이(LE)  +12 FEnet Position(0)
///     +13 BCC = 헤더 0..18 합계 &amp; 0xFF
///   응용 데이터
///     +00 02 01 | +02 Base | +03 Slot | +04 03 00 | +06 명령(LE) 1=OPEN 2=OPEN응답 4=CLOSE 7=SEND 8=SEND응답
///     OPEN     : 00 01 | 포트(LE) | 대상IP 4바이트 | 타임아웃(LE) | D0 07 | 00×4
///     OPEN응답 : 핸들(LE 4바이트)
///     SEND     : 핸들 | Modbus-TCP ADU          SEND응답 : Modbus-TCP ADU
///     CLOSE    : 핸들
///   안에 실리는 것은 표준 Modbus-TCP ADU 그대로다. 트랜잭션 ID 는 항상 0x0001, Unit ID 는 0xFF.
/// </summary>
public sealed class XgtTunnelClient : IAsyncDisposable
{
    public const int DefaultPort = 2004;
    private const int HeaderLen = 20;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LSIS-XGT");

    private readonly TcpClient _tcp = new() { NoDelay = true };
    private NetworkStream? _ns;
    private ushort _invoke;
    private uint _handle;
    private byte _base, _slot;
    private bool _opened;

    private NetworkStream Stream => _ns ?? throw new InvalidOperationException("ConnectAsync 를 먼저 호출해야 합니다.");

    // ---------------------------------------------------------------- 접속

    public async Task ConnectAsync(string viaIp, int viaPort, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await _tcp.ConnectAsync(viaIp, viaPort, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"경유 PLC 접속 타임아웃 ({viaIp}:{viaPort}, {timeoutMs}ms)");
        }
        _ns = _tcp.GetStream();
    }

    /// <summary>경유 모듈(Base/Slot)에게 대상 드라이브로의 Modbus-TCP 연결을 열게 한다.</summary>
    public async Task OpenAsync(int baseNo, int slotNo, string targetIp, int targetPort,
                                int driveTimeoutMs, int timeoutMs, CancellationToken ct)
    {
        _base = (byte)baseNo;
        _slot = (byte)slotNo;

        if (!IPAddress.TryParse(targetIp, out var ip) || ip.GetAddressBytes().Length != 4)
            throw new ArgumentException($"대상 IP 가 올바르지 않습니다: '{targetIp}'");
        var ipb = ip.GetAddressBytes();

        var app = new byte[24];
        WriteAppHeader(app, 0x0001);
        app[8] = 0x00; app[9] = 0x01;
        app[10] = (byte)targetPort; app[11] = (byte)(targetPort >> 8);
        ipb.CopyTo(app, 12);
        app[16] = (byte)driveTimeoutMs; app[17] = (byte)(driveTimeoutMs >> 8);
        app[18] = 0xD0; app[19] = 0x07;      // 캡처에서 항상 2000
        // 20..23 = 0

        var resp = await ExchangeAsync(app, timeoutMs, ct).ConfigureAwait(false);
        ushort cmd = (ushort)(resp[6] | (resp[7] << 8));
        if (cmd != 0x0002 || resp.Length < 12)
            throw new IOException($"경유 OPEN 실패 (응답 명령 0x{cmd:X4})");

        _handle = (uint)(resp[8] | (resp[9] << 8) | (resp[10] << 16) | (resp[11] << 24));
        if (_handle == 0)
            throw new IOException("경유 모듈이 연결 핸들을 돌려주지 않았습니다. Base/Slot 또는 드라이브 IP 를 확인하세요.");
        _opened = true;
    }

    // ---------------------------------------------------------------- 읽기

    public async Task<ushort[]> ReadRegistersAsync(
        byte unitId, byte functionCode, ushort address, ushort count, int timeoutMs, CancellationToken ct)
    {
        if (!_opened) throw new InvalidOperationException("OpenAsync 를 먼저 호출해야 합니다.");
        if (count is 0 or > 125) throw new ArgumentOutOfRangeException(nameof(count));

        var app = new byte[8 + 4 + 12];
        WriteAppHeader(app, 0x0007);
        app[8] = (byte)_handle; app[9] = (byte)(_handle >> 8);
        app[10] = (byte)(_handle >> 16); app[11] = (byte)(_handle >> 24);

        // Modbus-TCP ADU. 캡처에서 트랜잭션 ID 는 항상 0x0001 로 고정돼 있었다.
        int m = 12;
        app[m + 0] = 0x00; app[m + 1] = 0x01;      // Transaction Id
        app[m + 2] = 0x00; app[m + 3] = 0x00;      // Protocol Id
        app[m + 4] = 0x00; app[m + 5] = 0x06;      // Length
        app[m + 6] = unitId;
        app[m + 7] = functionCode;
        app[m + 8] = (byte)(address >> 8); app[m + 9] = (byte)address;
        app[m + 10] = (byte)(count >> 8); app[m + 11] = (byte)count;

        var resp = await ExchangeAsync(app, timeoutMs, ct).ConfigureAwait(false);
        ushort cmd = (ushort)(resp[6] | (resp[7] << 8));
        if (cmd != 0x0008)
            throw new IOException($"경유 SEND 응답 이상 (명령 0x{cmd:X4})");

        var mb = resp[8..];
        if (mb.Length < 9) throw new IOException($"Modbus 응답이 너무 짧습니다 ({mb.Length} bytes)");

        byte fc = mb[7];
        if ((fc & 0x80) != 0) throw new ModbusException(mb.Length > 8 ? mb[8] : (byte)0);
        if (fc != functionCode) throw new IOException($"기능코드 불일치 (요청 {functionCode}, 응답 {fc})");

        int byteCount = mb[8];
        if (byteCount != count * 2 || mb.Length < 9 + byteCount)
            throw new IOException($"응답 길이 이상 (byteCount={byteCount}, 기대={count * 2})");

        var regs = new ushort[count];
        for (int i = 0; i < count; i++)
            regs[i] = (ushort)((mb[9 + i * 2] << 8) | mb[10 + i * 2]);
        return regs;
    }

    // ---------------------------------------------------------------- 종료

    public async Task CloseAsync(int timeoutMs, CancellationToken ct)
    {
        if (!_opened) return;
        _opened = false;
        try
        {
            var app = new byte[12];
            WriteAppHeader(app, 0x0004);
            app[8] = (byte)_handle; app[9] = (byte)(_handle >> 8);
            app[10] = (byte)(_handle >> 16); app[11] = (byte)(_handle >> 24);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var frame = BuildFrame(++_invoke, app);
            await Stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
            await Stream.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch { /* 정리 과정의 오류는 삼킨다 — 핸들은 PLC 쪽 타임아웃으로도 회수된다 */ }
    }

    // ---------------------------------------------------------------- 저수준

    private void WriteAppHeader(byte[] app, ushort cmd)
    {
        app[0] = 0x02; app[1] = 0x01;
        app[2] = _base; app[3] = _slot;
        app[4] = 0x03; app[5] = 0x00;
        app[6] = (byte)cmd; app[7] = (byte)(cmd >> 8);
    }

    private static byte[] BuildFrame(ushort invokeId, byte[] app)
    {
        var f = new byte[HeaderLen + app.Length];
        Magic.CopyTo(f, 0);
        // 8..12 = 0 (Reserved / PLC Info / CPU Info)
        f[13] = 0x33;                                   // Source: PC → PLC
        f[14] = (byte)invokeId; f[15] = (byte)(invokeId >> 8);
        f[16] = (byte)app.Length; f[17] = (byte)(app.Length >> 8);
        f[18] = 0x00;                                   // FEnet Position
        int sum = 0;
        for (int i = 0; i < 19; i++) sum += f[i];
        f[19] = (byte)(sum & 0xFF);                     // BCC
        app.CopyTo(f, HeaderLen);
        return f;
    }

    /// <summary>요청을 보내고 같은 Invoke ID 의 응답 응용데이터를 돌려준다.</summary>
    private async Task<byte[]> ExchangeAsync(byte[] app, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var tok = cts.Token;

        try
        {
            ushort expect = ++_invoke;
            var frame = BuildFrame(expect, app);
            var ns = Stream;
            await ns.WriteAsync(frame, tok).ConfigureAwait(false);
            await ns.FlushAsync(tok).ConfigureAwait(false);

            // 다른 Invoke ID 의 프레임이 섞여 오면 흘려보낸다
            for (int guard = 0; guard < 16; guard++)
            {
                var header = await ReadExactlyAsync(HeaderLen, tok).ConfigureAwait(false);
                for (int i = 0; i < 8; i++)
                    if (header[i] != Magic[i]) throw new IOException("XGT 프레임 시그니처가 맞지 않습니다.");

                ushort invoke = (ushort)(header[14] | (header[15] << 8));
                int len = header[16] | (header[17] << 8);
                if (len is < 0 or > 4096) throw new IOException($"XGT 응용데이터 길이 이상 ({len})");

                var body = await ReadExactlyAsync(len, tok).ConfigureAwait(false);
                if (invoke == expect)
                {
                    if (body.Length < 8) throw new IOException($"XGT 응용데이터가 너무 짧습니다 ({body.Length} bytes)");
                    return body;
                }
            }
            throw new IOException("응답 Invoke ID 가 계속 맞지 않습니다.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"경유 응답 타임아웃 ({timeoutMs}ms)");
        }
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await Stream.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("경유 PLC 가 연결을 닫았습니다.");
            read += n;
        }
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync(300, CancellationToken.None).ConfigureAwait(false); } catch { }
        try { _ns?.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
    }
}
