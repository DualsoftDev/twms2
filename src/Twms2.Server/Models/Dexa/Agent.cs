namespace Twms2.Server.Models.Dexa;

public class Agent
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Ip { get; set; }
    public string? SwVersion { get; set; }
    public bool Online { get; set; }
    public DateTime Connected { get; set; }
    public DateTime? Disconnected { get; set; }
}
