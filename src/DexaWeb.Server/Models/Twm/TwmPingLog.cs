namespace DexaWeb.Server.Models.Twm;

/// <summary>
/// 핑 상태 변경 이력 (온라인↔오프라인 전환 시에만 기록).
/// </summary>
public class TwmPingLog
{
    public long Id { get; set; }
    public int DexaAssetId { get; set; }
    public bool Reachable { get; set; }
    public DateTime CheckedAt { get; set; }
}
