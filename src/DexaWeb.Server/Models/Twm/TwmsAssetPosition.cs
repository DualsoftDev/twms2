namespace DexaWeb.Server.Models.Twm;

/// <summary>배치도 자산 위치 (SVG viewBox 0 0 1000 600 기준).</summary>
public class TwmsAssetPosition
{
    public int      LayoutId  { get; set; }
    public int      AssetId   { get; set; }
    public double   X         { get; set; }
    public double   Y         { get; set; }
    public double   Scale     { get; set; } = 1.0;
    public bool     Visible   { get; set; }
    public DateTime UpdatedAt { get; set; }
}
