namespace Twms2.Server.Models.Dexa;

public class User
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Password { get; set; }
    public bool Admin { get; set; }
    public string? AugRoles { get; set; }
}
