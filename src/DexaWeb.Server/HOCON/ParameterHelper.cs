namespace DexaWeb.Server.HOCON;

/// <summary>
/// HOCON 파라미터 문자열 처리 유틸리티.
/// hkmc TwmApp.Shared.HOCON.ParameterHelper 에서 이식.
/// </summary>
public static class ParameterHelper
{
    private const string Sq = "'";
    private const string Dq = "\"";

    /// <summary>dot notation path 를 split</summary>
    internal static IEnumerable<string> SplitPath(string path) => path.Split('.');

    /// <summary>
    /// Path 를 분해해서 반환.  "a.b.setting.profile" ==> ("a.b.setting", "profile")
    /// </summary>
    internal static (string Path, string Name) DecomposeItemPath(string path)
    {
        var pathArr = SplitPath(path).ToArray();
        var count = pathArr.Length;
        var lpath = string.Join(".", pathArr.Take(count - 1));
        var name = pathArr.Last();
        return (lpath, name);
    }

    private static bool IsQuoted(string? str)
        => str?.Length >= 2
        && ((str.StartsWith(Dq) && str.EndsWith(Dq))
            || (str.StartsWith(Sq) && str.EndsWith(Sq)));

    public static string Unquote(string str)
        => IsQuoted(str)
            ? new string(str.Skip(1).Take(str.Length - 2).ToArray())
            : str;

    public static string Escape(string str)
    {
        var wrappedQ = IsQuoted(str);
        var spaced = str.Any(ch => ch == ' ' || ch == '\t');
        if (wrappedQ)
            str = new string(str.Skip(1).Take(str.Length - 2).ToArray());

        var escaped = str
            .Replace(@"\", @"\\")
            .Replace(@"""", @"\""");

        string doubleQuote(string s) => $"\"{s}\"";
        return (wrappedQ || spaced) ? doubleQuote(escaped) : escaped;
    }

    internal static string WrapQuoteOnDemand(string str, string wrapChar = "'")
        => IsQuoted(str) ? str : $"{wrapChar}{str}{wrapChar}";

    public static string UnwrapQuote(string? str)
        => str != null && IsQuoted(str)
            ? str.Substring(1, str.Length - 2)
            : str ?? "";

    public static string Decode(string? str)
        => str != null ? Unquote(str) : "";

    /// <summary>HOCON 파라미터 문자열에서 자산 이름 추출</summary>
    public static string? GetAssetName(this string? parameter)
    {
        if (string.IsNullOrEmpty(parameter))
            return null;

        var hocon = new Parameter(parameter);
        var raw = hocon[$"{hocon.AssetTypeName}.name.value"];
        return raw != null ? Decode(UnwrapQuote(raw)) : null;
    }

    /// <summary>HOCON 파라미터 문자열에서 자산 타입 이름 추출 ("plc", "hmi", "drive" 등)</summary>
    public static string? GetAssetTypeName(this string? parameter)
    {
        if (string.IsNullOrEmpty(parameter))
            return null;

        var hocon = new Parameter(parameter);
        return hocon.AssetTypeName;
    }
}
