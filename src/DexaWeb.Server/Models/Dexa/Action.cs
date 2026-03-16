namespace DexaWeb.Server.Models.Dexa;

public class DexaAction
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public int? Version { get; set; }
    public DateTime? Started { get; set; }
    public DateTime? Finished { get; set; }
    public bool? ContentsChanged { get; set; }
    public string? Memo { get; set; }
}
