namespace DexaWeb.Server.Models.Twm;

/// <summary>
/// PLC/Servo 연결 정보 (DEXA parameter에 없는 IP/via/base/slot).
/// assetTypeId == 6 (XGT PLC) 또는 7 (LS Servo) 전용.
/// </summary>
public class TwmsAssetConn
{
    public int DexaId { get; set; }
    public string AugIp { get; set; } = "";
    public string? AugIpVia { get; set; }
    public int AugBaseNumber { get; set; }
    public int? AugSlotNumber { get; set; }
    public int? AugIsRobotPLC { get; set; }  // PLC만 사용 (1=로봇PLC)
    public DateTime UpdatedAt { get; set; }
}
