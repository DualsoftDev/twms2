using System.Net.Sockets;

namespace Twms2.Server.Services.Drive;

/// <summary>Modbus 예외응답 (기능코드 | 0x80).</summary>
public sealed class ModbusException : Exception
{
    public byte Code { get; }
    public ModbusException(byte code) : base($"Modbus 예외응답 0x{code:X2} ({Describe(code)})") => Code = code;

    private static string Describe(byte c) => c switch
    {
        0x01 => "Illegal Function",
        0x02 => "Illegal Data Address",
        0x03 => "Illegal Data Value",
        0x04 => "Slave Device Failure",
        0x05 => "Acknowledge",
        0x06 => "Slave Device Busy",
        0x08 => "Memory Parity Error",
        0x0A => "Gateway Path Unavailable",
        0x0B => "Gateway Target Failed To Respond",
        _ => "Unknown",
    };
}

/// <summary>
/// 최소 Modbus-TCP 클라이언트 (드라이브 직접 접속용).
/// 한 대상당 접속 → 읽기 → 해제로 끝내는 전제라 연결을 유지하지 않는다 —
/// 게이트웨이의 동시 세션을 오래 물고 있지 않기 위해서다.
/// E:\DriveScanner 의 ModbusTcpClient 이식.
/// </summary>
public sealed class ModbusTcpClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private NetworkStream? _ns;
    private ushort _txId;

    private NetworkStream Stream => _ns ?? throw new InvalidOperationException("ConnectAsync 를 먼저 호출해야 합니다.");

    public async Task ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await _tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"드라이브 접속 타임아웃 ({host}:{port}, {timeoutMs}ms)");
        }
        _ns = _tcp.GetStream();
        _tcp.ReceiveTimeout = timeoutMs;
        _tcp.SendTimeout = timeoutMs;
    }

    /// <summary>FC03(Holding) 또는 FC04(Input) 로 레지스터를 읽는다.</summary>
    public async Task<ushort[]> ReadRegistersAsync(
        byte unitId, byte functionCode, ushort startAddress, ushort count, int timeoutMs, CancellationToken ct)
    {
        if (count is 0 or > 125) throw new ArgumentOutOfRangeException(nameof(count), "1~125 사이여야 합니다.");

        var tx = unchecked(++_txId);
        var req = new byte[12];
        req[0] = (byte)(tx >> 8); req[1] = (byte)tx;   // Transaction Id
        req[2] = 0; req[3] = 0;                         // Protocol Id
        req[4] = 0; req[5] = 6;                         // Length
        req[6] = unitId;
        req[7] = functionCode;
        req[8] = (byte)(startAddress >> 8); req[9] = (byte)startAddress;
        req[10] = (byte)(count >> 8); req[11] = (byte)count;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var tok = cts.Token;

        try
        {
            var ns = Stream;
            await ns.WriteAsync(req, tok).ConfigureAwait(false);
            await ns.FlushAsync(tok).ConfigureAwait(false);

            var header = await ReadExactlyAsync(6, tok).ConfigureAwait(false);
            int len = (header[4] << 8) | header[5];
            if (len is < 2 or > 260) throw new IOException($"MBAP 길이 이상 ({len})");

            var body = await ReadExactlyAsync(len, tok).ConfigureAwait(false);
            byte fc = body[1];

            if ((fc & 0x80) != 0) throw new ModbusException(body.Length > 2 ? body[2] : (byte)0);
            if (fc != functionCode) throw new IOException($"기능코드 불일치 (요청 {functionCode}, 응답 {fc})");

            int byteCount = body[2];
            if (byteCount != count * 2 || body.Length < 3 + byteCount)
                throw new IOException($"응답 길이 이상 (byteCount={byteCount}, 기대={count * 2})");

            var regs = new ushort[count];
            for (int i = 0; i < count; i++)
                regs[i] = (ushort)((body[3 + i * 2] << 8) | body[4 + i * 2]);
            return regs;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"드라이브 응답 타임아웃 ({timeoutMs}ms)");
        }
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await Stream.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("드라이브가 연결을 닫았습니다.");
            read += n;
        }
        return buf;
    }

    public ValueTask DisposeAsync()
    {
        try { _ns?.Dispose(); } catch { /* 정리 중 오류는 무시 */ }
        try { _tcp.Dispose(); } catch { /* 정리 중 오류는 무시 */ }
        return ValueTask.CompletedTask;
    }
}
