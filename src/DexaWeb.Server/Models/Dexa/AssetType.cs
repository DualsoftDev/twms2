namespace DexaWeb.Server.Models.Dexa;

public class AssetType
{
    public int? Id { get; set; }
    public string? UserFriendlyName { get; set; }
    public string? Guid { get; set; }
    public bool Fake { get; set; }
    public string? Icon { get; set; }
    public string? Parameter { get; set; }
    public string? DotnetClassName { get; set; }
    public string? Description { get; set; }
}
