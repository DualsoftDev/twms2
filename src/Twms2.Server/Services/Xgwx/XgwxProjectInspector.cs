using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Twms2.Server.Services.Xgwx;

/// <summary>
/// XG5000 프로젝트(.xgwx)에서 DEXA 백업 접속에 쓰이는 정보를 읽어 낸다.
///
/// DEXA 는 프로젝트의 <c>Project/Configurations/Configuration/OnlineElements/XGCommSettings</c>
/// (속성명 <c>Connnections</c>, LS 원문 철자) 958바이트 블록만 보고 XG5000 에 접속한다.
/// 블록 레이아웃(2026-10 실측, XGI 구형·CPUZ·XGK 공통):
///   0x003 접속 매체 03=Ethernet 02=USB / 0x004 Ethernet 로컬 02·리모트1단 03·USB 01
///   0x066 1단 IP(ASCII 16B) / 0x076 포트(LE, 2002) / 0x095 경유 모듈 Base·Slot 니블 / 0x09E 2단 대상 IP
/// USB 로 바꿔 저장해도 IP 문자열은 남으므로 "IP 유무" 가 아니라 매체 바이트로 판정해야 한다.
///
/// 파일 포맷: XML 저장본, 또는 XG5000 4.8x 기본 바이너리(138B MFC 헤더 "XG\0" + gzip XML 본문).
/// 기종 코드(<c>Configuration Type</c>)가 연결 PLC 와 다르면 XG5000 이 접속을 거부하므로 함께 보고한다.
/// </summary>
public static class XgwxProjectInspector
{
    public const string ConnectionsAttribute = "Connnections";
    private const int BlobLength = 958;
    private const int OffMedium = 0x003, OffMode = 0x004, OffIp1 = 0x066, OffPort = 0x076, OffHopNetwork = 0x094, OffHopBaseSlot = 0x095, OffIp2 = 0x09E;

    public static XgwxInspection Inspect(byte[] bytes, string? fileName = null)
    {
        if (bytes is null || bytes.Length == 0)
            return XgwxInspection.Failed("빈 파일입니다.");

        string xml;
        string format;
        try
        {
            if (IsBinaryContainer(bytes))
            {
                xml = ExtractXmlFromBinary(bytes);
                format = "binary";
            }
            else if (LooksLikeXml(bytes))
            {
                xml = DecodeXml(bytes);
                format = "xml";
            }
            else
            {
                return XgwxInspection.Failed("XG5000 프로젝트 형식이 아닙니다 (XML 도, 바이너리 컨테이너도 아님).");
            }
        }
        catch (Exception ex)
        {
            return XgwxInspection.Failed("프로젝트 파일을 풀지 못했습니다: " + ex.Message);
        }

        XgwxInspection r;
        try { r = ParseXml(xml); }
        catch (XmlException ex) { return XgwxInspection.Failed("XG5000 프로젝트 XML 로 읽을 수 없습니다: " + ex.Message); }
        r.Format = format;
        r.FileName = fileName;
        return r;
    }

    // ── 컨테이너 ────────────────────────────────────────────────────

    /// <summary>4.8x 바이너리 저장본: "XG\0" 매직으로 시작하고 안에 "XG5000 WORKSPACE FILE" UTF-16 문자열이 있다.</summary>
    private static bool IsBinaryContainer(byte[] b) =>
        b.Length > 16 && b[0] == (byte)'X' && b[1] == (byte)'G' && b[2] == 0;

    private static bool LooksLikeXml(byte[] b)
    {
        int i = 0;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) i = 3;   // UTF-8 BOM
        while (i < b.Length && (b[i] == ' ' || b[i] == '\t' || b[i] == '\r' || b[i] == '\n')) i++;
        return i < b.Length && b[i] == (byte)'<';
    }

    private static string DecodeXml(byte[] b)
    {
        // XG5000 XML 저장본은 UTF-8. BOM 이 있으면 StreamReader 가 처리한다.
        using var ms = new MemoryStream(b);
        using var sr = new StreamReader(ms, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>첫 gzip 스트림(1F 8B 08)을 찾아 푼다. 실측 파일은 전부 오프셋 138 이었지만 헤더 길이에 기대지 않는다.</summary>
    private static string ExtractXmlFromBinary(byte[] b)
    {
        int off = -1;
        for (int i = 0; i < Math.Min(b.Length - 3, 4096); i++)
        {
            if (b[i] == 0x1F && b[i + 1] == 0x8B && b[i + 2] == 0x08) { off = i; break; }
        }
        if (off < 0) throw new InvalidDataException("바이너리 컨테이너 안에서 gzip 본문을 찾지 못했습니다.");

        using var ms = new MemoryStream(b, off, b.Length - off);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var sr = new StreamReader(gz, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    // ── XML ─────────────────────────────────────────────────────────

    private static XgwxInspection ParseXml(string xml)
    {
        var r = new XgwxInspection { Ok = false };
        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(new StringReader(xml), settings);

        bool sawProject = false, sawComm = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.Name)
            {
                case "Project" when !sawProject:
                    sawProject = true;
                    r.FileVer = reader.GetAttribute("FileVer");
                    r.FileLastWriteTime = reader.GetAttribute("FileLastWriteTime");
                    // <Project ...>이름<NetworkConfiguration> — 이름은 첫 텍스트 노드
                    if (!reader.IsEmptyElement)
                    {
                        reader.Read();
                        if (reader.NodeType == XmlNodeType.Text) r.ProjectName = reader.Value.Trim();
                    }
                    break;

                case "Configuration" when r.PlcTypeCode is null:
                    if (int.TryParse(reader.GetAttribute("Type"), out var code))
                    {
                        r.PlcTypeCode = code;
                        r.PlcTypeName = XgPlcTypes.NameOf(code);
                        r.PlcFamily = XgPlcTypes.FamilyOf(code);
                    }
                    if (int.TryParse(reader.GetAttribute("Kind"), out var kind)) r.ConfigurationKind = kind;
                    break;

                case "XGCommSettings" when !sawComm:
                    sawComm = true;
                    var hex = reader.GetAttribute(ConnectionsAttribute);
                    if (!string.IsNullOrWhiteSpace(hex)) DecodeConnectionBlock(hex.Trim(), r);
                    break;
            }
            if (sawProject && sawComm && r.PlcTypeCode is not null) break;
        }

        if (!sawProject) { r.Error = "XG5000 프로젝트 XML 이 아닙니다 (Project 요소 없음)."; return r; }
        if (!sawComm) r.Warnings.Add("접속 설정(XGCommSettings)이 없습니다. XG5000 에서 접속 설정을 한 번 저장해야 합니다.");
        if (r.PlcTypeCode is null) r.Warnings.Add("기종 코드(Configuration Type)를 찾지 못했습니다.");
        else if (r.PlcTypeName is null) r.Warnings.Add($"기종 코드 {r.PlcTypeCode} 는 알려진 표에 없습니다. XG5000 버전에 따라 다를 수 있습니다.");

        r.Ok = r.Medium == "ethernet" && !string.IsNullOrEmpty(r.Ip);
        return r;
    }

    private static void DecodeConnectionBlock(string hex, XgwxInspection r)
    {
        byte[] b;
        try { b = Convert.FromHexString(hex); }
        catch { r.Warnings.Add("접속 설정 블록이 16진수 형식이 아닙니다."); return; }
        if (b.Length < OffIp2 + 16) { r.Warnings.Add($"접속 설정 블록 길이가 짧습니다 ({b.Length}B, 기대 {BlobLength}B)."); return; }
        r.ConnectionBlockLength = b.Length;

        byte medium = b[OffMedium], mode = b[OffMode];
        r.Medium = medium switch { 0x03 => "ethernet", 0x02 => "usb", _ => "other" };
        r.MediumCode = medium; r.ModeCode = mode;

        string ip1 = ReadAscii(b, OffIp1, 16);
        string ip2 = ReadAscii(b, OffIp2, 16);
        r.Port = b[OffPort] | (b[OffPort + 1] << 8);

        if (r.Medium == "ethernet" && mode == 0x03)
        {
            // 리모트 1단: 1단 주소는 경유 PLC, 경유 PLC 의 통신 모듈(Base/Slot)을 거쳐 2단 대상 IP 로.
            r.Stage = 2;
            r.ViaIp = NullIfEmpty(ip1);
            r.ViaBase = (b[OffHopBaseSlot] >> 4) & 0xF;
            r.ViaSlot = b[OffHopBaseSlot] & 0xF;
            r.Ip = NullIfEmpty(ip2);
            if (r.ViaIp is null) r.Warnings.Add("2단 접속인데 경유 IP 가 비어 있습니다.");
            if (r.Ip is null) r.Warnings.Add("2단 접속인데 대상 IP 가 비어 있습니다.");
        }
        else
        {
            r.Stage = 1;
            r.Ip = NullIfEmpty(ip1);
            // 양식에서 2단 흔적이 남아 있을 수 있다(HMC CPUUN 양식). 표시용으로만 보관한다.
            r.StaleSecondaryIp = NullIfEmpty(ip2);
        }

        switch (r.Medium)
        {
            case "usb":
                r.Warnings.Add("이 프로젝트는 USB 접속으로 저장되어 있습니다. DEXA 는 Ethernet 으로 접속하므로 IP 를 확인해 주세요."
                               + (r.Ip is not null ? $" (파일에 남아 있는 IP: {r.Ip})" : ""));
                break;
            case "other":
                r.Warnings.Add($"이 프로젝트의 접속 방식(코드 0x{medium:X2})은 Ethernet 이 아닙니다. IP 를 확인해 주세요.");
                break;
            default:
                if (r.Ip is null) r.Warnings.Add("Ethernet 접속이지만 IP 가 비어 있습니다.");
                if (r.Port != 2002) r.Warnings.Add($"접속 포트가 {r.Port} 입니다 (기본 2002).");
                break;
        }
    }

    private static string ReadAscii(byte[] b, int off, int len)
    {
        int end = off;
        while (end < off + len && end < b.Length && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b, off, end - off).Trim();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>해석 결과. API 응답에 그대로 직렬화된다.</summary>
public sealed class XgwxInspection
{
    /// <summary>Ethernet 접속이고 대상 IP 가 있으면 true — DEXA 가 그대로 쓸 수 있는 접속 설정.</summary>
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; } = new();

    public string? FileName { get; set; }
    public string? Format { get; set; }               // xml | binary
    public string? FileVer { get; set; }
    public string? FileLastWriteTime { get; set; }
    public string? ProjectName { get; set; }

    public int? PlcTypeCode { get; set; }
    public string? PlcTypeName { get; set; }          // 예: XGI-CPUUN
    public string? PlcFamily { get; set; }            // XGI | XGK | XGB | ...
    public int? ConfigurationKind { get; set; }

    public int? ConnectionBlockLength { get; set; }
    public string? Medium { get; set; }               // ethernet | usb | other
    public int? MediumCode { get; set; }
    public int? ModeCode { get; set; }
    public int Stage { get; set; }                    // 1 = 로컬, 2 = 리모트 1단(경유), 0 = 블록 없음
    public string? Ip { get; set; }                   // 백업 대상 PLC IP
    public int? Port { get; set; }
    public string? ViaIp { get; set; }
    public int? ViaBase { get; set; }
    public int? ViaSlot { get; set; }
    public string? StaleSecondaryIp { get; set; }

    public static XgwxInspection Failed(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// XG5000 기종 코드(<c>Configuration Type</c>) ↔ 이름. 출처: XG5000 4.81 CMDDB.mdb tblPLCType.
/// 코드 707 은 4.81 접속 메시지에서 "XGC-CPUX7" 로 표시됐다 — 표가 설치본 버전에 따라 다를 수 있으니 참고용이다.
/// </summary>
public static class XgPlcTypes
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [0] = "XGK-CPUH", [1] = "XGK-CPUS", [3] = "XGK-CPUA", [4] = "XGK-CPUE", [5] = "XGK-CPUU",
        [14] = "XGK-CPUUN", [16] = "XGK-CPUHN", [17] = "XGK-CPUSN",
        [2] = "XGB-XBMS", [6] = "XGB-DR16C3", [7] = "XGB-XBCH", [8] = "XGB-DR32HL", [9] = "XGB-XBCE", [10] = "XGB-XBCS",
        [12] = "XGB-XBCEB", [13] = "XGB-XBCEX", [15] = "XGB-XBCU", [18] = "XGB-XBMH", [19] = "XGB-XBMHP", [20] = "XBC-ELC",
        [21] = "XGB-XBMH2", [22] = "XGB-XBCXS",
        [100] = "XGI-CPUU", [101] = "XGR-CPUH", [102] = "XGI-CPUH", [103] = "XGB-XECH", [104] = "XGI-CPUS", [105] = "XGR-INC",
        [106] = "XGI-CPUE", [107] = "XGI-CPUU/D", [108] = "XGB-XECS", [109] = "XGB-XECE", [110] = "XGI-CPUS/P", [111] = "XGI-CPUUN",
        [112] = "XGB-XECU", [113] = "XGB-KL", [114] = "XGB-GIPAM", [115] = "XGB-XEMHP", [116] = "XGB-XEMH2",
        [400] = "XGS-CPU01A", [450] = "XGL-SCPU",
        [600] = "XMC-E32A", [601] = "XMC-E32C", [602] = "XMC-E08A", [603] = "XMC-E16A", [604] = "LSMMT-E32A", [605] = "LSMMT-E32C", [650] = "XGF-XM32E",
        [700] = "XGI-CPUZ7", [703] = "XGI-CPUZ3", [705] = "XGI-CPUZ5", [707] = "XGI-CPUZ7C", [750] = "XGB-XECZ",
    };

    public static string? NameOf(int code) => Names.TryGetValue(code, out var n) ? n : null;

    public static string? FamilyOf(int code)
    {
        var n = NameOf(code);
        if (n is null) return null;
        int dash = n.IndexOf('-');
        return dash > 0 ? n[..dash] : n;
    }
}
