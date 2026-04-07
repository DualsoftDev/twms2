using System.Diagnostics;
using System.Text.RegularExpressions;
using Hocon;
using static Twms2.Server.HOCON.ParameterItem;

namespace Twms2.Server.HOCON;

/// <summary>
/// HOCON configuration string 을 분석해서 자료 구조화.
/// flat/nested 양 형식 모두 HoconParser.Parse() 로 통합 파싱.
/// hkmc TwmApp.Shared.HOCON.Parameter 에서 이식.
/// </summary>
public class Parameter
{
    public string AssetTypeName { get; private set; } = "";
    public ParameterItem[] Items = [];
    public Dictionary<string, string>? _dict;

    public IEnumerable<string> Paths => Items.Select(i => i.Path);
    public IEnumerable<string> PathKeys => Items.Select(i => i.PathKey);

    public string? this[string key]
    {
        get
        {
            if (_dict == null)
            {
                _dict = Items
                    .SelectMany(it => it.ToKeyValuePairs())
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            }
            return _dict.TryGetValue(key, out var val) ? val : null;
        }
    }

    public Parameter(string? hocon)
    {
        if (string.IsNullOrEmpty(hocon))
        {
            _dict = new();
            Items = [];
            return;
        }

        var kvs = EmHOCON.Flatten(hocon);
        _dict = kvs.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var pathKeys = kvs.SelectCol1();
        var roots = pathKeys
            .Select(p => ParameterHelper.SplitPath(p).First())
            .Distinct()
            .ToArray();

        Debug.Assert(roots.Length == 1);
        AssetTypeName = roots[0];

        var items =
            from kv in kvs
            let k = Regex.Replace(kv.Key, @$"^{Regex.Escape(AssetTypeName)}\.", "")
            let v = kv.Value
            let newkv = new KeyValueEx(k, v)
            let grouper = ParameterHelper.DecomposeItemPath(k).Path
            group newkv by grouper into gr
            select new ParameterItem(AssetTypeName, gr.Key, gr.ToList());

        Items = items.ToArray();
    }

    /// <summary>Items 를 flat HOCON string 으로 변환</summary>
    public string ItemsToString()
    {
        var hocons = Items.Select(p => p.ToHOCONString());
        return string.Join("\r\n", hocons);
    }

    /// <summary>
    /// 단위 key value pairs 로부터 beautified HOCON config string 생성.
    /// key : hocon path + key.  e.g "plc.name.value"
    /// value : e.g "PLC backup asset"
    /// </summary>
    public static string Buildup(IEnumerable<(string Key, string Value)> tpls)
    {
        var hocon = string.Join("\r\n", tpls.Select(tpl => $"{tpl.Key} = {tpl.Value ?? "{}"}"));
        return HoconParser.Parse(hocon).ToString();
    }

    public void SetAssetTypeName(string newAssetTypeName, string? desiredAssetName = null)
    {
        AssetTypeName = newAssetTypeName;
        foreach (var pi in Items)
            pi.AssetName = newAssetTypeName;
        var nameItem = Items.FirstOrDefault(pi => pi.Key == "name");
        if (nameItem != null)
            nameItem.Value = desiredAssetName ?? newAssetTypeName;
    }
}
