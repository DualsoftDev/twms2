namespace DexaWeb.Server.Models.Twm;

public class TwmMigration
{
    public int Version { get; set; }
    public string? Description { get; set; }
    public DateTime AppliedAt { get; set; }
}
