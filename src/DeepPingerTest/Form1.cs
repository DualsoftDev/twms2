using System.Runtime.InteropServices;
using System.Text.Json;

namespace DeepPingerTest;

public partial class Form1 : Form
{
    [DllImport("DeepPinger.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int DeepPing(
        string targetIpString, string pingIpString,
        int nBase, int nSlot, int timeoutSec);

    private static readonly string SettingsPath = Path.Combine(
        AppContext.BaseDirectory, "settings.json");

    public Form1()
    {
        InitializeComponent();
        LoadSettings();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SaveSettings();
        base.OnFormClosed(e);
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (s == null) return;

            if (s.TryGetValue("Ip", out var ip)) txtIp.Text = ip.GetString();
            if (s.TryGetValue("ViaIp", out var via)) txtViaIp.Text = via.GetString();
            if (s.TryGetValue("Base", out var b)) numBase.Value = b.GetInt32();
            if (s.TryGetValue("Slot", out var sl)) numSlot.Value = sl.GetInt32();
            if (s.TryGetValue("Timeout", out var t)) numTimeout.Value = t.GetInt32();
        }
        catch { }
    }

    private void SaveSettings()
    {
        var s = new Dictionary<string, object>
        {
            ["Ip"] = txtIp.Text.Trim(),
            ["ViaIp"] = txtViaIp.Text.Trim(),
            ["Base"] = (int)numBase.Value,
            ["Slot"] = (int)numSlot.Value,
            ["Timeout"] = (int)numTimeout.Value
        };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
    }

    private async void BtnTest_Click(object? sender, EventArgs e)
    {
        var ip = txtIp.Text.Trim();
        var viaIp = txtViaIp.Text.Trim();

        if (string.IsNullOrEmpty(ip))
        {
            MessageBox.Show("IP (핑 대상)를 입력해주세요.", "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txtIp.Focus();
            return;
        }
        if (string.IsNullOrEmpty(viaIp))
        {
            MessageBox.Show("ViaIP (게이트웨이)를 입력해주세요.", "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txtViaIp.Focus();
            return;
        }

        int baseNum = (int)numBase.Value;
        int slotNum = (int)numSlot.Value;
        int timeout = (int)numTimeout.Value;

        btnTest.Enabled = false;
        lblStatus.Text = "핑 테스트 중...";
        lblStatus.ForeColor = Color.Orange;
        txtResult.Clear();

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int errorCode = await Task.Run(() => DeepPing(viaIp, ip, baseNum, slotNum, timeout));
            sw.Stop();

            var category = GetCategory(errorCode);
            var description = GetDescription(errorCode);
            bool success = errorCode == 0;

            var hint = GetHint(errorCode);
            txtResult.Text =
                $"에러코드: {errorCode}\r\n" +
                $"카테고리: {category}\r\n" +
                $"설명: {description}\r\n" +
                (hint != null ? $"원인: {hint}\r\n" : "") +
                $"소요시간: {sw.ElapsedMilliseconds}ms";

            lblStatus.Text = success ? "성공" : $"실패 (코드: {errorCode})";
            lblStatus.ForeColor = success ? Color.Green : Color.Red;

            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var resultMark = success ? "OK" : "FAIL";
            txtLog.AppendText($"[{timestamp}] [{resultMark}] ViaIP={viaIp}, IP={ip}, Base={baseNum}, Slot={slotNum} → 코드={errorCode} ({description}) [{sw.ElapsedMilliseconds}ms]\r\n");
        }
        catch (DllNotFoundException)
        {
            txtResult.Text = "오류: DeepPinger.dll을 찾을 수 없습니다.\r\n실행 파일과 같은 폴더에 DLL을 복사해주세요.";
            lblStatus.Text = "DLL 없음";
            lblStatus.ForeColor = Color.Red;
        }
        catch (Exception ex)
        {
            txtResult.Text = $"오류: {ex.Message}";
            lblStatus.Text = "오류 발생";
            lblStatus.ForeColor = Color.Red;
        }
        finally
        {
            btnTest.Enabled = true;
        }
    }

    private static string GetCategory(int code) => code switch
    {
        0 => "OK",
        >= 1 and <= 99 => "Connection",
        >= 100 and <= 199 => "DataTransfer",
        >= 200 and <= 299 => "Protocol",
        >= 300 and <= 399 => "Diagnostics",
        _ => "Unknown"
    };

    private static string GetDescription(int code) => code switch
    {
        0 => "성공",
        1 => "WSAStartup 실패",
        2 => "Winsock 버전 불일치",
        3 => "소켓 생성 실패",
        4 => "소켓 바인드 실패",
        5 => "IP 주소 해석 실패",
        6 => "PLC 연결 타임아웃 (포트 2004)",
        7 => "WSACreateEvent 실패",
        8 => "WSAEventSelect 실패",
        100 => "데이터 전송 실패",
        101 => "데이터 수신 타임아웃",
        200 => "Pass-Through 응답 없음",
        201 => "CompanyID 불일치",
        202 => "InvokeID 불일치",
        203 => "Command 불일치",
        204 => "체크섬 검증 실패",
        205 => "PLC Pass-Through 에러",
        206 => "예상치 못한 Frame Command",
        300 => "진단 응답 없음",
        301 => "하부장비 핑 에러",
        302 => "하부장비 핑 성공횟수 불일치",
        _ => $"알 수 없는 에러 ({code})"
    };

    private static string? GetHint(int code) => code switch
    {
        0 => null,
        1 => "Windows 소켓 라이브러리 초기화에 실패했습니다. 시스템 리소스를 확인하세요.",
        2 => "Winsock 2.2 버전이 필요합니다. Windows 업데이트를 확인하세요.",
        3 => "TCP 소켓을 생성할 수 없습니다. 다른 프로그램이 리소스를 점유 중일 수 있습니다.",
        4 => "소켓 바인드 실패. 포트 충돌이 있을 수 있습니다.",
        5 => "ViaIP 주소 형식이 잘못되었습니다. IP 형식(예: 192.168.1.1)을 확인하세요.",
        6 => "ViaIP(게이트웨이 PLC)의 2004 포트에 연결할 수 없습니다. PLC 전원, 네트워크 연결, IP 주소를 확인하세요.",
        7 => "WSA 이벤트 생성 실패. 시스템 리소스가 부족할 수 있습니다.",
        8 => "WSA 이벤트 등록 실패. 소켓 상태를 확인하세요.",
        100 => "PLC에 패킷을 전송하지 못했습니다. 네트워크 연결이 끊어졌을 수 있습니다.",
        101 => "PLC로부터 응답이 없습니다. PLC가 요청을 처리하지 못했거나 네트워크가 불안정합니다.",
        200 => "PLC 응답 데이터가 비어있습니다. PLC 상태를 확인하세요.",
        201 => "응답의 CompanyID가 'LSIS-XGT'가 아닙니다. 다른 장비가 응답했을 수 있습니다.",
        202 => "요청/응답 InvokeID 불일치. 이전 요청의 응답이 뒤늦게 도착했을 수 있습니다.",
        203 => "응답 Command가 Tunneling(0x0102)이 아닙니다. PLC가 터널링을 지원하지 않을 수 있습니다.",
        204 => "응답 패킷의 체크섬이 일치하지 않습니다. 통신 중 데이터가 손상되었을 수 있습니다.",
        205 => "PLC Pass-Through 처리 중 에러가 발생했습니다. Base/Slot 값이 잘못되었거나 하부 장비와 통신할 수 없습니다.",
        206 => "PLC가 진단 응답(FrameCmd=2) 대신 다른 응답을 보냈습니다. Base/Slot 값을 확인하거나, PLC가 진단 터널링을 지원하는지 확인하세요.",
        300 => "PLC를 통과했으나 진단 응답 데이터가 비어있습니다. 하부 장비가 응답하지 않았습니다.",
        301 => "하부 장비 핑에서 에러가 발생했습니다. 대상 IP 주소가 정확한지, 장비 전원이 켜져 있는지 확인하세요.",
        302 => "핑 패킷이 전송되었으나 성공 응답을 받지 못했습니다. 대상 장비의 네트워크 상태를 확인하세요.",
        _ => null
    };
}
