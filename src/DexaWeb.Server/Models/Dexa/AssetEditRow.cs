using DexaWeb.Server.Models.Twm;

namespace DexaWeb.Server.Models.Dexa;

/// <summary>
/// 자산 편집 행 모델. DEXA parameter 필드 + TWM aug/conn 필드 통합.
/// IsModified* 프로퍼티로 저장소별 변경 추적.
/// </summary>
public class AssetEditRow
{
    public int    AssetId     { get; init; }
    public int    AssetTypeId { get; init; }
    public string TypeName    { get; init; } = "";

    internal EditableAsset DexaAsset { get; init; } = null!;

    // ── DEXA parameter 필드 ─────────────────────────────
    public string Name        { get; set; } = "";
    public string Ip          { get; set; } = "";   // FTP/Drive/XP: HOCON; PLC/Servo: 미사용
    public string Description { get; set; } = "";
    public string Agent       { get; set; } = "";   // agentPreferences (;구분)
    public string ModelName    { get; set; } = "";   // Drive: HOCON modelName (e.g. iS7)
    public string ModelVersion { get; set; } = "";   // Drive: HOCON modelVersion

    // ── TWM 공통 확장 정보 ──────────────────────────────
    public int?    StationNumber { get; set; }
    public string? Vendor        { get; set; }
    public string? Spec          { get; set; }
    public int?    LineId        { get; set; }
    public string  LineName      { get; set; } = "";

    // ── 통합 IP (유형별 자동 매핑) ──────────────────────────
    /// <summary>FTP/Drive/XP → Ip, PLC/Servo → ConnIp 자동 선택.</summary>
    public string DisplayIp
    {
        get => AssetTypeId is 6 or 7 ? ConnIp : Ip;
        set { if (AssetTypeId is 6 or 7) ConnIp = value; else Ip = value; }
    }

    // ── 연결 정보 (PLC/Servo: TwmsAssetConn, Drive: HOCON) ──
    public string  ConnIp      { get; set; } = "";
    public string? ConnIpVia   { get; set; }
    public int     ConnBase    { get; set; }
    public int?    ConnSlot    { get; set; }
    public int?    ConnIsRobot { get; set; }   // PLC 전용
    public bool    ConnViaEnabled { get; set; } // Drive 전용: 경유 연결 활성 (HOCON visible)

    // ── 변경 추적 스냅샷 ─────────────────────────────────
    private string  _sName = "", _sIp = "", _sDesc = "", _sAgent = "", _sModelName = "", _sModelVersion = "";
    private int?    _sStation;
    private string? _sVendor, _sSpec;
    private int?    _sLineId;
    private string  _sConnIp = "";
    private string? _sConnIpVia;
    private int     _sConnBase;
    private int?    _sConnSlot, _sConnIsRobot;
    private bool    _sConnViaEnabled;

    public bool IsDexaModified =>
        Name != _sName || Ip != _sIp || Description != _sDesc
        || Agent != _sAgent || ModelName != _sModelName || ModelVersion != _sModelVersion
        || ConnViaEnabled != _sConnViaEnabled;

    public bool IsTwmAugModified =>
        StationNumber != _sStation || Vendor != _sVendor ||
        Spec != _sSpec || LineId != _sLineId;

    public bool IsTwmConnModified =>
        ConnIp != _sConnIp || ConnIpVia != _sConnIpVia ||
        ConnBase != _sConnBase || ConnSlot != _sConnSlot || ConnIsRobot != _sConnIsRobot;

    public bool IsModified => IsDexaModified || IsTwmAugModified || IsTwmConnModified;

    public static AssetEditRow From(ViewAsset va, TwmsAsset? aug, TwmsAssetConn? conn)
    {
        var name  = va.Name  ?? "";
        var ip    = va.Ip    ?? "";
        var desc  = va.Description ?? "";
        var agent = va.AssetAgentPreferences ?? "";
        var cIp    = conn?.AugIp    ?? "";
        // conn (PLC/Servo) 우선, HOCON 파싱값 (Drive) fallback
        var cVia   = conn?.AugIpVia      ?? va.AugIpVia;
        var cBase  = conn?.AugBaseNumber  ?? va.AugBaseNumber ?? 0;
        var cSlot  = conn?.AugSlotNumber  ?? va.AugSlotNumber;
        var cRobot = conn?.AugIsRobotPLC;
        var modelName = va.ModelName ?? "";
        var modelVer = va.ModelVersion ?? "";

        var row = new AssetEditRow
        {
            AssetId     = va.AssetId,
            AssetTypeId = va.AssetTypeId ?? 0,
            TypeName    = va.AssetTypeUserFriendlyName ?? "",
            DexaAsset   = EditableAsset.FromViewAsset(va),
            Name = name, Ip = ip, Description = desc, Agent = agent,
            ModelName = modelName, ModelVersion = modelVer,
            StationNumber = aug?.AugStationNumber,
            Vendor  = aug?.AugVendor, Spec = aug?.AugSpec, LineId = aug?.AugLineId,
            ConnIp  = cIp,   ConnIpVia = cVia,
            ConnBase = cBase, ConnSlot = cSlot, ConnIsRobot = cRobot,
            ConnViaEnabled = va.ViaEnabled ?? true,
        };
        row._sName = name; row._sIp = ip; row._sDesc = desc; row._sAgent = agent;
        row._sModelName = modelName; row._sModelVersion = modelVer;
        row._sConnViaEnabled = row.ConnViaEnabled;
        row._sStation = aug?.AugStationNumber;
        row._sVendor = aug?.AugVendor; row._sSpec = aug?.AugSpec; row._sLineId = aug?.AugLineId;
        row._sConnIp = cIp; row._sConnIpVia = cVia;
        row._sConnBase = cBase; row._sConnSlot = cSlot; row._sConnIsRobot = cRobot;
        return row;
    }

    public void MarkSaved()
    {
        _sName = Name; _sIp = Ip; _sDesc = Description; _sAgent = Agent;
        _sModelName = ModelName; _sModelVersion = ModelVersion;
        _sStation = StationNumber; _sVendor = Vendor; _sSpec = Spec; _sLineId = LineId;
        _sConnIp = ConnIp; _sConnIpVia = ConnIpVia;
        _sConnBase = ConnBase; _sConnSlot = ConnSlot; _sConnIsRobot = ConnIsRobot;
        _sConnViaEnabled = ConnViaEnabled;
    }
}
