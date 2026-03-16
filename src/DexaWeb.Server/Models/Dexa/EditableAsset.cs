using DexaWeb.Server.HOCON;

namespace DexaWeb.Server.Models.Dexa;

/// <summary>
/// HOCON 파라미터를 파싱하여 편집 가능한 구조로 노출.
/// Parameter/ParameterItem 기반으로 flat/nested 양 형식 완전 지원.
/// 수정 후 BuildParameter() 로 HOCON 문자열 재구성.
/// </summary>
public class EditableAsset
{
    public int AssetId { get; init; }
    public string TypeName { get; init; } = "";
    public int? AssetTypeId { get; init; }

    /// <summary>에이전트 선호도 (세미콜론 구분)</summary>
    public string AgentPreferences { get; set; } = "";

    /// <summary>원본 HOCON 파라미터 문자열</summary>
    public string OriginalParameter { get; init; } = "";

    /// <summary>원본 에이전트 선호도</summary>
    public string OriginalAgentPreferences { get; init; } = "";

    /// <summary>파싱된 Parameter 객체 (null = empty/unparseable)</summary>
    public Parameter? Parameter { get; private set; }

    /// <summary>편집용 ParameterItem 배열</summary>
    public ParameterItem[] Items { get; set; } = [];

    private ParameterItem[] _originalItems = [];

    /// <summary>
    /// UI 호환용 computed Fields.
    /// Key = ParameterItem.Key, Value = decoded value.
    /// Button 타입은 제외.
    /// </summary>
    public Dictionary<string, string> Fields
    {
        get
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items)
            {
                if (item.DataType == DataType.Button) continue;
                dict.TryAdd(item.Key, ParameterHelper.Decode(ParameterHelper.UnwrapQuote(item.Value)));
            }
            return dict;
        }
    }

    /// <summary>수정 여부</summary>
    public bool IsModified
    {
        get
        {
            if (AgentPreferences != OriginalAgentPreferences)
                return true;

            if (Items.Length != _originalItems.Length)
                return true;

            for (int i = 0; i < Items.Length; i++)
            {
                if (Items[i].Value != _originalItems[i].Value)
                    return true;
            }
            return false;
        }
    }

    /// <summary>C# ViewAsset에서 EditableAsset 생성</summary>
    public static EditableAsset FromViewAsset(ViewAsset va)
    {
        var param = va.AssetParameter ?? "";
        Parameter? parsed = null;
        ParameterItem[] items = [];

        if (!string.IsNullOrEmpty(param))
        {
            try
            {
                parsed = new Parameter(param);
                items = parsed.Items;
            }
            catch
            {
                // malformed HOCON — items 빈 배열
            }
        }

        var originalItems = items.Select(it => it.Duplicate()).ToArray();

        return new EditableAsset
        {
            AssetId = va.AssetId,
            TypeName = va.AssetTypeUserFriendlyName ?? "",
            AssetTypeId = va.AssetTypeId,
            AgentPreferences = va.AssetAgentPreferences ?? "",
            OriginalAgentPreferences = va.AssetAgentPreferences ?? "",
            OriginalParameter = param,
            Parameter = parsed,
            Items = items,
            _originalItems = originalItems,
        };
    }

    /// <summary>특정 필드의 값을 수정 (Key 기반 첫 번째 매칭 항목)</summary>
    public void SetField(string key, string value)
    {
        var item = Items.FirstOrDefault(it =>
            it.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
            && it.DataType != DataType.Button);
        if (item != null)
            item.Value = value;
    }

    /// <summary>섹션의 visible 플래그 수정 (PathKey 기반)</summary>
    public void SetSectionVisible(string pathKey, string visible)
    {
        var item = Items.FirstOrDefault(it =>
            it.PathKey.Equals(pathKey, StringComparison.OrdinalIgnoreCase));
        if (item != null)
            item.Visible = visible;
    }

    /// <summary>수정된 Items 를 HOCON 문자열로 재구성</summary>
    public string BuildParameter()
    {
        if (Items.Length == 0)
            return OriginalParameter;

        try
        {
            var tpls = Items.SelectMany(it => it.ToKeyValuePairs());
            return Parameter.Buildup(tpls);
        }
        catch
        {
            return OriginalParameter;
        }
    }

    /// <summary>모든 자산에서 편집 가능한 필드 키 목록 수집 (Button 제외)</summary>
    public static HashSet<string> CollectAllKeys(IEnumerable<EditableAsset> assets)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
            foreach (var item in asset.Items)
                if (item.DataType != DataType.Button)
                    keys.Add(item.Key);
        return keys;
    }
}
