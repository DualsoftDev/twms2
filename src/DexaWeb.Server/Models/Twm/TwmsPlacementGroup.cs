namespace DexaWeb.Server.Models.Twm;

/// <summary>
/// 사용자 정의 배치 그룹.
/// 자산들을 시각적으로 묶어 한 번에 이동/관리.
/// </summary>
public class TwmsPlacementGroup
{
    public int      Id        { get; set; }
    public int      LayoutId  { get; set; }
    public string   Name      { get; set; } = "";
    public double   X         { get; set; }
    public double   Y         { get; set; }
    public double   Width     { get; set; } = 150;
    public double   Height    { get; set; } = 100;
    public string?  Color     { get; set; }
    public DateTime UpdatedAt { get; set; }
}
