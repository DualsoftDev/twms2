namespace DexaWeb.Server.Models.Dexa;

/// <summary>
/// 에셋 실시간 상태 (asset_status 테이블).
/// DEXA 2.20 Topic 시스템이 주기적으로 갱신.
/// </summary>
public class AssetStatus
{
    public int AssetId { get; set; }
    public int Available { get; set; }
    public DateTime? Time { get; set; }
    public string? Status { get; set; }
    public string? Detail { get; set; }
}
