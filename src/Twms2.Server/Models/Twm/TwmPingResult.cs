namespace Twms2.Server.Models.Twm;

public class TwmPingResult
{
    public int DexaAssetId { get; set; }
    public string? IpAddress { get; set; }
    public bool Reachable { get; set; }
    public int? RoundtripMs { get; set; }
    public DateTime CheckedAt { get; set; }
}
