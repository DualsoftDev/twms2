namespace Twms2.Server.Models.Dexa;

public class Trigger
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? CronExpression { get; set; }
    public bool Enabled { get; set; }
    public string? Description { get; set; }
}
