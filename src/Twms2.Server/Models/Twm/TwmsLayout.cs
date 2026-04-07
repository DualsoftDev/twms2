namespace Twms2.Server.Models.Twm;

/// <summary>레이아웃 정의 (도면/배치도 구성 단위).</summary>
public class TwmsLayout
{
    public int      Id        { get; set; }
    public string   Name      { get; set; } = "";
    public int      SortOrder { get; set; }
    public DateTime UpdatedAt { get; set; }
}
