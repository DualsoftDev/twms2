using System.Reflection;
using System.Text;

namespace XgwxMaker;

// 템플릿(TEMP_CPUUN.xml / TEMP_CPUS.xml) 을 읽어 IP/viaIP/slot/base 를 치환하고 .xgwx 로 저장.
// Asset.cs (원본 hkmc.totalwebmanagement 프로젝트) 의 치환 규칙을 그대로 옮김.
// 템플릿은 어셈블리에 임베디드 리소스로 포함되어 있어 단일 EXE 로 배포 가능.
internal static class XgwxBuilder
{
    private const string CpuunResource = "XgwxMaker.templates.TEMP_CPUUN.xml";
    private const string CpusResource  = "XgwxMaker.templates.TEMP_CPUS.xml";

    // 템플릿 내 원본 패턴
    private const string ViaIpOldHex  = "31302E3134332E35322E323034000000D207";             // "10.143.52.204"
    private const string TargetOldHex = "0600000000000000003134302E32302E32362E3131003232"; // 6번 슬롯 "140.20.26.11"

    public static void Make(string outPath, string ip, string? viaIp, int? slot, int? baseNo)
    {
        if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("IP 는 필수", nameof(ip));

        string content;
        if (!string.IsNullOrWhiteSpace(viaIp))
        {
            if (slot is null || baseNo is null)
                throw new ArgumentException("ViaIP 가 있으면 Slot 과 Base 가 필수");
            if (slot < 0 || slot > 15) throw new ArgumentOutOfRangeException(nameof(slot), "Slot 은 0..15");
            if (baseNo < 0 || baseNo > 15) throw new ArgumentOutOfRangeException(nameof(baseNo), "Base 는 0..15");

            content = ReadResource(CpusResource);
            content = content.Replace(ViaIpOldHex, ConvertViaIpToHex(viaIp!));
            var baseSlot = baseNo.Value.ToString("X") + slot.Value.ToString("X") + "0000000000000000";
            content = content.Replace(TargetOldHex, baseSlot + ConvertTargetIpToHex(ip));
        }
        else
        {
            content = ReadResource(CpuunResource);
            content = content.Replace(ViaIpOldHex, ConvertViaIpToHex(ip));
        }

        // 프로젝트 이름을 출력 파일명(확장자 제외)으로 맞춤. CPUS=ROBOT, CPUUN=AUTOLAND광명2 위치.
        var projectName = Path.GetFileNameWithoutExtension(outPath);
        if (!string.IsNullOrWhiteSpace(projectName))
            content = ReplaceProjectName(content, projectName);

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outPath, content);
    }

    // <Project ...>NAME<NetworkConfiguration> 형태에서 NAME 부분만 교체.
    private static string ReplaceProjectName(string xml, string newName)
    {
        const string suffix = "<NetworkConfiguration>";
        int suffixIdx = xml.IndexOf(suffix, StringComparison.Ordinal);
        if (suffixIdx < 0) return xml;
        int prefixEnd = xml.LastIndexOf('>', suffixIdx - 1); // <Project ...> 의 닫는 '>'
        if (prefixEnd < 0) return xml;
        return xml[..(prefixEnd + 1)] + XmlEscape(newName) + xml[suffixIdx..];
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string ReadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"임베디드 리소스 없음: {name}. 사용 가능: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ConvertViaIpToHex(string ip)
    {
        var hex = IpToAsciiHexWithDots(ip);
        while (hex.Length < 32) hex.Append("00");
        hex.Append("D207");
        return hex.ToString();
    }

    private static string ConvertTargetIpToHex(string ip)
    {
        var hex = IpToAsciiHexWithDots(ip);
        while (hex.Length < 32) hex.Append("00");
        hex.Append("020000");
        return hex.ToString();
    }

    private static StringBuilder IpToAsciiHexWithDots(string ip)
    {
        var hex = new StringBuilder();
        foreach (var octet in ip.Split('.'))
        {
            foreach (var c in octet) hex.AppendFormat("{0:X2}", (int)c);
            hex.Append("2E");
        }
        if (hex.Length >= 2) hex.Remove(hex.Length - 2, 2);
        return hex;
    }

    // ── CSV ─────────────────────────────────────────────────────────
    public sealed record Row(string Name, string Ip, string? ViaIp, int? Slot, int? Base);

    public static List<Row> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count < 2) throw new InvalidDataException("CSV 는 헤더 + 1행 이상 필요");

        var headers = SplitCsv(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int idxName = headers.IndexOf("name");
        int idxIp   = headers.IndexOf("ip");
        int idxVia  = headers.IndexOf("viaip");
        int idxSlot = headers.IndexOf("slot");
        int idxBase = headers.IndexOf("base");
        if (idxName < 0 || idxIp < 0) throw new InvalidDataException("CSV 헤더에 Name, Ip 가 있어야 함");

        var rows = new List<Row>();
        for (int i = 1; i < lines.Count; i++)
        {
            var cells = SplitCsv(lines[i]);
            string Cell(int ix) => ix >= 0 && ix < cells.Count ? cells[ix].Trim() : "";
            var via = idxVia >= 0 ? Cell(idxVia) : "";
            rows.Add(new Row(
                Name:  Cell(idxName),
                Ip:    Cell(idxIp),
                ViaIp: string.IsNullOrWhiteSpace(via) ? null : via,
                Slot:  int.TryParse(Cell(idxSlot), out var s) ? s : null,
                Base:  int.TryParse(Cell(idxBase), out var b) ? b : null));
        }
        return rows;
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuote)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuote = false;
                }
                else cur.Append(ch);
            }
            else
            {
                if (ch == ',') { result.Add(cur.ToString()); cur.Clear(); }
                else if (ch == '"') inQuote = true;
                else cur.Append(ch);
            }
        }
        result.Add(cur.ToString());
        return result;
    }
}
