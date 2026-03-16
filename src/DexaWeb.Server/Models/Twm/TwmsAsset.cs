namespace DexaWeb.Server.Models.Twm;

/// <summary>
/// 자산 공통 확장 정보 (전 타입).
/// IP/연결 정보는 TwmsAssetConn 참조.
/// </summary>
public class TwmsAsset
{
    public int DexaId { get; set; }

    // 전체 자산 공통
    public int? AugStationNumber { get; set; }
    public string? AugVendor { get; set; }
    public string? AugSpec { get; set; }
    public int? AugLineId { get; set; }

    public DateTime UpdatedAt { get; set; }
}
