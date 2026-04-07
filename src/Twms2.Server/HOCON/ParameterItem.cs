using System.Diagnostics;

namespace Twms2.Server.HOCON;

/// <summary>
/// Parameter 의 최종 하나의 항목을 표현.  e.g "plc.setting.project"
/// parameter UI 에서 하나의 행에 해당.
/// hkmc TwmApp.Shared.HOCON.ParameterItem 에서 이식 + DEXA 원본 Visible 추가.
/// </summary>
public class ParameterItem
{
    /// <summary>Data type.  DataType.File @ "plc.setting.project"</summary>
    public DataType DataType { get; set; }

    /// <summary>root name.  "plc" @ "plc.setting.project"</summary>
    public string AssetName { get; set; } = "";

    /// <summary>root 제외하고 자신까지의 path.  "setting" @ "plc.setting.project"</summary>
    public string Path { get; set; } = "";

    /// <summary>key.  "project" @ "plc.setting.project"</summary>
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";

    public string? MinMax { get; set; }

    /// <summary>visible 플래그 (DEXA UI 용). round-trip 보존.</summary>
    public string? Visible { get; set; }

    public string DecodedValue => ParameterHelper.Decode(Value);

    /// <summary>Combo box selection 이 필요한 경우.  e.g "[TCP, UDP]"</summary>
    public string? CandidateString { get; set; }

    public IEnumerable<string> Candidates =>
        (CandidateString ?? "")
        .TrimStart('[')
        .TrimEnd(']')
        .Split(new[] { ", ", "," }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>DataType.File 인 경우의 extension filter</summary>
    public string? FileFilter { get; set; }

    /// <summary>Path + Key.  "setting.project" @ "plc.setting.project"</summary>
    public string PathKey => string.IsNullOrEmpty(Path) ? Key : $"{Path}.{Key}";

    /// <summary>AssetName + Path + Key.  e.g "plc.setting.project"</summary>
    public string FullPathKey => $"{AssetName}.{PathKey}";

    public KeyValueEx[] KeyValues = [];

    public class KeyValueEx
    {
        public KeyValueEx(string key, string value)
        {
            Key = key;
            Value = value;
        }
        public string Key { get; set; }
        public string Value { get; set; }
    }

    public ParameterItem(string assetName, string key, IEnumerable<KeyValueEx> subFields)
    {
        KeyValues = subFields.ToArray();
        AssetName = assetName;

        var paths = subFields
            .Select(kv => kv.Key)
            .Select(ParameterHelper.DecomposeItemPath)
            .Select(t => t.Path)
            .Distinct()
            .ToArray();

        Debug.Assert(paths.Length == 1);
        var commonPath = paths[0];
        (Path, Key) = ParameterHelper.DecomposeItemPath(commonPath);

        foreach (var kv in subFields)
        {
            var k = kv.Key.Replace($"{commonPath}.", "");
            var v = kv.Value;
            switch (k)
            {
                case "value":
                    Value = v?.Replace(@"\\", @"\") ?? "";
                    break;
                case "minmax":
                    MinMax = v;
                    break;
                case "type":
                    DataType = v?.ToLower() switch
                    {
                        "int" => DataType.Numeric,
                        "numeric" => DataType.Numeric,
                        _ => Enum.TryParse<DataType>(v, out var dt) ? dt : DataType.String
                    };
                    break;
                case "filter":
                    FileFilter = v;
                    break;
                case "candidates":
                    CandidateString = v;
                    break;
                case "visible":
                    Visible = v;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown Key {k} when parsing HOCON parameter item {kv.Key}={kv.Value}");
                    break;
            }
        }
    }

    private ParameterItem() { }

    public ParameterItem Duplicate() => new()
    {
        DataType = DataType,
        AssetName = AssetName,
        Path = Path,
        Key = Key,
        Value = Value,
        CandidateString = CandidateString,
        FileFilter = FileFilter,
        MinMax = MinMax,
        Visible = Visible,
        KeyValues = KeyValues.ToArray()
    };

    /// <summary>내부 구조를 HOCON string 으로 변환</summary>
    public string ToHOCONString()
    {
        var lines = ToKeyValuePairs().Select(kv => $"{kv.Key} = {kv.Value}");
        return string.Join("\r\n", lines);
    }

    public IEnumerable<(string Key, string Value)> ToKeyValuePairs()
    {
        yield return ($"{FullPathKey}.type", $"{DataType}");
        yield return ($"{FullPathKey}.value", ParameterHelper.WrapQuoteOnDemand(ParameterHelper.Escape(Value)));
        if (!string.IsNullOrEmpty(MinMax))
            yield return ($"{FullPathKey}.minmax", ParameterHelper.Escape(MinMax));
        if (!string.IsNullOrEmpty(Visible))
            yield return ($"{FullPathKey}.visible", ParameterHelper.Escape(Visible));
        if (!string.IsNullOrEmpty(FileFilter))
            yield return ($"{FullPathKey}.filter", ParameterHelper.Escape(FileFilter));
        if (!string.IsNullOrEmpty(CandidateString))
            yield return ($"{FullPathKey}.candidates", CandidateString);
    }

    public override string ToString() => $"{PathKey}={Value}";
}
