namespace Twms2.Server.Models.Dexa;

public class ActionLog
{
    public int Id { get; set; }
    public int ActionId { get; set; }
    public string? Level { get; set; }
    public string? Message { get; set; }
    public DateTime? DateTime { get; set; }
    public int? Thread { get; set; }
}
