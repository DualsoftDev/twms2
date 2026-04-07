namespace Twms2.Server.Models.Twm;

public class TwmDbStats
{
    public int SchemaVersion    { get; set; }
    public int AssetAugCount    { get; set; }
    public int AssetConnCount   { get; set; }
    public int LayoutLineCount  { get; set; }
    public int LayoutGroupCount { get; set; }
}
