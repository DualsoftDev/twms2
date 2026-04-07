using Hocon;

namespace Twms2.Server.HOCON;

/// <summary>
/// HOCON 파싱 확장 메서드.
/// HoconParser.Parse() 기반으로 flat/nested 양 형식을 동일하게 처리.
/// hkmc TwmApp.Shared.HOCON.EmHOCON 에서 이식.
/// </summary>
public static class EmHOCON
{
    /// <summary>
    /// HOCON 문자열을 flat key-value 쌍으로 변환.
    /// nested 형식이든 flat 형식이든 동일한 결과.
    /// </summary>
    public static IEnumerable<(string Key, string Value)> Flatten(string hocon)
    {
        var config = HoconParser.Parse(hocon);
        return ExtractKeyValues(config.Value);

        static IEnumerable<(string, string)> ExtractKeyValues(IHoconElement ele)
        {
            switch (ele)
            {
                case HoconObject folder:
                    if (folder.Count == 0)
                    {
                        yield return (folder.Path.ToString(), null!);
                    }
                    else
                    {
                        foreach (var ob in folder)
                        {
                            var (k, v) = (ob.Key, ob.Value);
                            if (v.Type != HoconType.Object)
                                yield return ($"{folder.Path}.{k}", v.ToString());

                            foreach (var tpl in ExtractKeyValues(v.Value))
                                yield return tpl;
                        }
                    }
                    break;

                case HoconValue hv:
                    foreach (var tpl in hv.Children.SelectMany(child => ExtractKeyValues(child)))
                        yield return tpl;
                    break;
            }
        }
    }

    /// <summary>HOCON 문자열을 case-insensitive dictionary 로 변환</summary>
    public static Dictionary<string, string> ToDictionary(string hocon)
        => Flatten(hocon).ToDictionary(
            tpl => tpl.Key,
            tpl => tpl.Value,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// HOCON library PrettyPrint 의 backslash 처리 보정.
    /// PrettyPrint 가 \\ 를 \ 로 바꾸는 문제 대응.
    /// </summary>
    public static string ModifiedPrettyPrint(this HoconRoot root, int indentSize)
        => root.PrettyPrint(indentSize).Replace(@"\", @"\\");

    // --- tuple projection helpers ---

    public static IEnumerable<T1> SelectCol1<T1, T2>(this IEnumerable<(T1, T2)> source)
        => source.Select(tpl => tpl.Item1);

    public static IEnumerable<T2> SelectCol2<T1, T2>(this IEnumerable<(T1, T2)> source)
        => source.Select(tpl => tpl.Item2);

    /// <summary>
    /// Parameter 에서 특정 키의 decoded value 를 가져온다.
    /// key 예: "name", "connections.IP", "setting.project"
    /// </summary>
    public static string? GetHoconValue(this Parameter param, string key)
    {
        var raw = param[$"{param.AssetTypeName}.{key}.value"];
        return raw != null ? ParameterHelper.Decode(ParameterHelper.UnwrapQuote(raw)) : null;
    }
}
