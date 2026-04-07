using System.Text.RegularExpressions;

namespace Twms2.Server.HOCON;

/// <summary>
/// HOCON 문자열 regex 기반 직접 치환 헬퍼.
/// 전체 파싱 없이 특정 값만 빠르게 교체할 때 사용.
/// hkmc TwmApp.Shared.HOCON.EmHOCONRegex 에서 이식.
/// </summary>
public static class EmHOCONRegex
{
    /// <summary>connections > IP > value 치환 (nested HOCON 용)</summary>
    public static string ReplaceIp(string hocon, string newIp)
    {
        string pattern = @"(connections\s*{[^}]*IP\s*{[^}]*value\s*=\s*')([^']*)(')";
        return Regex.Replace(hocon, pattern,
            match => $"{match.Groups[1].Value}{newIp}{match.Groups[3].Value}");
    }

    /// <summary>max count > value 치환 (nested HOCON 용)</summary>
    public static string ReplaceMaxCountValue(string hocon, int newValue)
    {
        string pattern = @"(max count\s*{[^}]*value\s*=\s*')([^']*)(')";
        return Regex.Replace(hocon, pattern,
            match => $"{match.Groups[1].Value}{newValue}{match.Groups[3].Value}");
    }
}
