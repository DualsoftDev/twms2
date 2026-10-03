using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twms2.Server.Services.Drive;

/// <summary>
/// 드라이브 식별에 어느 레지스터를 읽어 어떻게 해석할지에 대한 정의.
/// 내장 기본값을 쓰되, ProgramData\DualSoft\TWMS2\drive-profile.json 이 있으면 그것으로 덮어쓴다 —
/// 처음 보는 기종에서 주소가 다르면 재배포 없이 현장에서 고칠 수 있게 하기 위해서다.
///
/// 근거 (E:\DriveScanner 에서 확정)
///  1) via_1.pcapng 캡처 (경유 접속, iS7 1.04)
///       0x0300 → 11      기종 코드 (iS7)
///       0x0E0B → 104     모델버전 ÷100 → 1.04   ← DriveView 등록정보 "버전", .INV 파일명
///       0x0303 → 0x4717  Inverter SW Version → 71.23 (바이트별 10진)
///  2) DriveView9 DataFile\*.inv 헤더 66개 전부
///       +0x80C 기종 코드, +0x810 기종 이름, +0x84C 0x0300, +0x850 0x0E0B (13개 시리즈 전부 동일)
/// 실장비로 끝까지 확인된 기종은 iS7 하나다. 나머지는 DataFile 근거다.
/// </summary>
public sealed class DriveProfile
{
    public const string FileName = "drive-profile.json";

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 3;
    [JsonPropertyName("read")] public ReadSpec Read { get; set; } = new();

    /// <summary>기종코드(10진 문자열) → 시리즈.</summary>
    [JsonPropertyName("series")] public Dictionary<string, SeriesSpec> Series { get; set; } = new();

    public sealed class ReadSpec
    {
        [JsonPropertyName("functionCode")] public byte FunctionCode { get; set; } = 3;

        /// <summary>캡처에서 항상 0xFF 였다. 국번은 Modbus 계층에서 쓰이지 않는다.</summary>
        [JsonPropertyName("unitId")] public byte UnitId { get; set; } = 0xFF;

        /// <summary>.inv 파라미터 "Inverter Model". 65개 파일 전부 0x0300.</summary>
        [JsonPropertyName("modelAddr"), JsonConverter(typeof(HexUShortConverter))]
        public ushort ModelAddr { get; set; } = 0x0300;

        /// <summary>모델버전. .inv 헤더 +0x850, 65개 파일 전부 0x0E0B.</summary>
        [JsonPropertyName("modelVersionAddr"), JsonConverter(typeof(HexUShortNullableConverter))]
        public ushort? ModelVersionAddr { get; set; } = 0x0E0B;

        /// <summary>.inv 파라미터 "Inverter SW Version". 65개 파일 전부 0x0303.</summary>
        [JsonPropertyName("invSwVersionAddr"), JsonConverter(typeof(HexUShortNullableConverter))]
        public ushort? InvSwVersionAddr { get; set; } = 0x0303;
    }

    public sealed class SeriesSpec
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";

        /// <summary>기본 주소와 다른 기종이 나오면 여기서 덮어쓴다. 보통은 null.</summary>
        [JsonPropertyName("modelVersionAddr"), JsonConverter(typeof(HexUShortNullableConverter))]
        public ushort? ModelVersionAddr { get; set; }
    }

    // ---------------------------------------------------------------- 로드

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string OverridePath => Path.Combine(TwmsDataPath.Base, FileName);

    /// <summary>덮어쓰기 파일이 있으면 그것, 없거나 깨졌으면 내장 기본값.</summary>
    public static DriveProfile Load(ILogger? logger = null)
    {
        var path = OverridePath;
        if (!File.Exists(path)) return Default();
        try
        {
            var p = JsonSerializer.Deserialize<DriveProfile>(File.ReadAllText(path), JsonOpts);
            if (p is not null) return p;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "{Path} 를 읽지 못해 내장 드라이브 프로파일을 씁니다", path);
        }
        return Default();
    }

    /// <summary>DataFile\*.inv 헤더 +0x80C / +0x810 에서 추출한 기종 코드표.</summary>
    public static DriveProfile Default() => new()
    {
        Series = new Dictionary<string, SeriesSpec>
        {
            ["5"]  = new() { Name = "iV5" },
            ["6"]  = new() { Name = "S100" },
            ["7"]  = new() { Name = "iV5L" },
            ["9"]  = new() { Name = "iP5A" },
            ["10"] = new() { Name = "iG5A" },
            ["11"] = new() { Name = "iS7" },        // 캡처로 실측 확인
            ["12"] = new() { Name = "C100" },
            ["14"] = new() { Name = "M100" },
            // 코드 15 는 H100 과 초기 H100Plus(602.00)가 공유한다. 602.01 부터는 20 이다.
            ["15"] = new() { Name = "H100" },
            ["16"] = new() { Name = "G100" },
            ["17"] = new() { Name = "S300" },       // DataFile\S300\*.json 의 "ModelNo": 17
            ["18"] = new() { Name = "L100" },
            ["20"] = new() { Name = "H100Plus" },
        },
    };

    // ---------------------------------------------------------------- 표기

    /// <summary>상·하위 바이트를 각각 10진수로. 0x4717 → "71.23". Inverter SW Version 이 이 형식.</summary>
    public static string FormatByteVersion(ushort v) => $"{v >> 8}.{v & 0xFF:D2}";

    /// <summary>값을 100 으로 나눈다. 104 → "1.04", 110 → "1.10". 모델버전이 이 형식이며 DEXA modelVersion 표기와 같다.</summary>
    public static string FormatHundredths(ushort v) => $"{v / 100}.{v % 100:D2}";

    public SeriesSpec? LookupSeries(ushort modelCode) =>
        Series.TryGetValue(modelCode.ToString(CultureInfo.InvariantCulture), out var s) ? s : null;

    public string SeriesName(ushort modelCode) =>
        LookupSeries(modelCode)?.Name is { Length: > 0 } n ? n : $"Unknown({modelCode})";

    /// <summary>해당 기종에 쓸 모델버전 주소. 시리즈 재정의가 있으면 그것을 우선한다.</summary>
    public ushort? ModelVersionAddrFor(ushort modelCode) =>
        LookupSeries(modelCode)?.ModelVersionAddr ?? Read.ModelVersionAddr;
}

/// <summary>profile.json 에서 주소를 "0x0300" 처럼 쓸 수 있게 해준다. 숫자도 그대로 받는다.</summary>
public sealed class HexUShortConverter : JsonConverter<ushort>
{
    public override ushort Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => HexNum.Parse(ref r) ?? 0;
    public override void Write(Utf8JsonWriter w, ushort v, JsonSerializerOptions o) => w.WriteStringValue($"0x{v:X4}");
}

public sealed class HexUShortNullableConverter : JsonConverter<ushort?>
{
    public override ushort? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => HexNum.Parse(ref r);
    public override void Write(Utf8JsonWriter w, ushort? v, JsonSerializerOptions o)
    {
        if (v.HasValue) w.WriteStringValue($"0x{v.Value:X4}");
        else w.WriteNullValue();
    }
}

internal static class HexNum
{
    public static ushort? Parse(ref Utf8JsonReader r)
    {
        if (r.TokenType == JsonTokenType.Null) return null;
        if (r.TokenType == JsonTokenType.Number) return r.GetUInt16();
        var s = r.GetString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt16(s[2..], 16);
            if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt16(s[..^1], 16);
            return ushort.Parse(s, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }
}
