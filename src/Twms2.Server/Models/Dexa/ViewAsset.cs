using System.Text.RegularExpressions;
using Twms2.Server.HOCON;
using Twms2.Server.Models.Twm;

namespace Twms2.Server.Models.Dexa;

/// <summary>
/// DEXA asset + assetType 직접 조회 모델.
/// assetParameter (HOCON 형식)에서 이름/IP 등을 파싱 (primary).
/// Aug* 필드는 TwmsAsset.ApplyAug()로 채움 (secondary).
/// </summary>
public partial class ViewAsset
{
    // --- asset 컬럼 ---
    public int AssetId { get; set; }
    public int AssetParentId { get; set; }
    public string? AssetAgentPreferences { get; set; }
    public string? AssetParameter { get; set; }
    public bool AssetDeleted { get; set; }

    // --- assetType 컬럼 ---
    public int? AssetTypeId { get; set; }
    public string? AssetTypeUserFriendlyName { get; set; }
    public bool AssetTypeFake { get; set; }

    // --- Aug 확장 필드 (ApplyAug()로 채워짐) ---
    /// <summary>IP 주소. HOCON 파싱 우선, TwmsAssetConn.AugIp 폴백.</summary>
    public string? AugIp { get; set; }
    public int? AugStationNumber { get; set; }
    public int? AugBaseNumber { get; set; }
    public int? AugSlotNumber { get; set; }
    public string? AugVendor { get; set; }
    public string? AugSpec { get; set; }
    public string? AugIpVia { get; set; }
    public int? AugIsRobotPLC { get; set; }
    public int? AugLineId { get; set; }

    /// <summary>Drive 경유 연결 활성 여부 (HOCON visible 플래그). null = 비해당.</summary>
    public bool? ViaEnabled { get; private set; }
    public string? LayoutLineName { get; set; }

    /// <summary>TwmsAsset (공통) + TwmsAssetConn (연결정보) 병합.</summary>
    public void ApplyAug(TwmsAsset? asset, TwmsAssetConn? conn = null)
    {
        // ParseParameter()가 먼저 실행되도록 보장
        _ = Name;

        if (asset != null)
        {
            AugStationNumber = asset.AugStationNumber;
            AugVendor        = asset.AugVendor;
            AugSpec          = asset.AugSpec;
            AugLineId        = asset.AugLineId;
        }

        if (conn != null)
        {
            // IP: HOCON 우선, TwmsAssetConn 폴백
            if (AugIp == null) AugIp = conn.AugIp;

            // ViaIp/Base/Slot: HOCON 우선, TwmsAssetConn 폴백
            if (!_hoconHasViaIp) AugIpVia      = conn.AugIpVia;
            if (!_hoconHasBase)  AugBaseNumber  = conn.AugBaseNumber;
            if (!_hoconHasSlot)  AugSlotNumber  = conn.AugSlotNumber;

            AugIsRobotPLC = conn.AugIsRobotPLC;
        }
    }

    // --- 폴더 판별 ---
    public bool IsSystemRootFolder => AssetTypeId == 1;
    public bool IsNonRootFolder => AssetTypeId == 2;
    public bool IsFolder => IsSystemRootFolder || IsNonRootFolder;
    public bool IsGroup => AssetTypeId == 101;
    public bool IsRealAsset => !IsFolder && !AssetTypeFake && !IsGroup;

    // --- assetParameter 파싱 결과 (캐싱) ---
    private bool _parsed;
    private string? _name;
    private string? _ip;
    private string? _description;
    private string? _modelVersion;
    private string? _modelName;
    private bool _hoconHasViaIp, _hoconHasBase, _hoconHasSlot;

    /// <summary>자산 이름 (assetParameter에서 파싱)</summary>
    public string? Name => ParseAndGet(ref _name);

    /// <summary>IP 주소 (HOCON parameter 우선, TwmsAsset.AugIp 폴백)</summary>
    public string? Ip => ParseAndGet(ref _ip) ?? AugIp;

    /// <summary>설명 (assetParameter에서 파싱)</summary>
    public string? Description => ParseAndGet(ref _description);

    /// <summary>모델명 (HOCON connections.Destination.modelName, Drive 전용)</summary>
    public string? ModelName => ParseAndGet(ref _modelName);

    /// <summary>모델 버전 (HOCON connections.Destination.modelVersion)</summary>
    public string? ModelVersion => ParseAndGet(ref _modelVersion);

    /// <summary>표시용 이름 (HOCON Name → 타입명 순서로 폴백)</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Name)) return Name;
            if (IsSystemRootFolder) return "Root";
            if (IsNonRootFolder) return "Folder";
            return AssetTypeUserFriendlyName ?? $"Asset #{AssetId}";
        }
    }

    private string? ParseAndGet(ref string? field)
    {
        if (!_parsed) ParseParameter();
        return field;
    }

    private void ParseParameter()
    {
        _parsed = true;
        if (string.IsNullOrEmpty(AssetParameter)) return;

        try
        {
            var param = new Parameter(AssetParameter);
            _name = param.GetHoconValue("name");
            _ip = param.GetHoconValue("connections.IP")
                  ?? param.GetHoconValue("connections.Destination.IP")
                  ?? param.GetHoconValue("IP");
            _description = param.GetHoconValue("description");

            // DEXA parameter 우선: via1_connection (flat) 또는 Via Connections 구조
            var viaIp = param.GetHoconValue("via1_connection")
                        ?? param.GetHoconValue("connections.Via Connections.1 depth.connection");
            if (viaIp != null) { AugIpVia = viaIp; _hoconHasViaIp = true; }

            var baseStr = param.GetHoconValue("via1_base")
                          ?? param.GetHoconValue("connections.Via Connections.1 depth.base number");
            if (baseStr != null && int.TryParse(baseStr, out var b)) { AugBaseNumber = b; _hoconHasBase = true; }

            var slotStr = param.GetHoconValue("via1_slot")
                          ?? param.GetHoconValue("connections.Via Connections.1 depth.slot number");
            if (slotStr != null && int.TryParse(slotStr, out var s)) { AugSlotNumber = s; _hoconHasSlot = true; }

            _modelName = param.GetHoconValue("connections.Destination.modelName");
            _modelVersion = param.GetHoconValue("connections.Destination.modelVersion");

            // Drive: 경유 연결 활성 여부 (visible 플래그는 .value 아님 — 직접 접근)
            var viaVisStr = param[$"{param.AssetTypeName}.connections.Via Connections.1 depth.visible"];
            if (viaVisStr != null)
                ViaEnabled = string.Equals(viaVisStr, "True", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // malformed HOCON fallback → regex
            _name = ExtractValue(AssetParameter, @"\.name\.value\s*=\s*");
            _ip = ExtractValue(AssetParameter, @"\.IP\.value\s*=\s*")
                  ?? ExtractValue(AssetParameter, @"IP\.value\s*=\s*");
            _description = ExtractValue(AssetParameter, @"\.description\.value\s*=\s*");
        }
    }

    /// <summary>HOCON regex fallback: key = 'value' 또는 key = "value" 또는 key = value</summary>
    private static string? ExtractValue(string text, string keyPattern)
    {
        var match = Regex.Match(text, keyPattern + @"['""]?([^'""$\r\n]+)['""]?", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
