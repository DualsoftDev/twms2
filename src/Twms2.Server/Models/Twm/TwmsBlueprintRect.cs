namespace Twms2.Server.Models.Twm;

/// <summary>도면 위 라인 영역 사각형 위치 (좌표: 이미지 대비 %).</summary>
public class TwmsBlueprintRect
{
    public int      LayoutId  { get; set; }
    public int      LineId    { get; set; }
    public double   X         { get; set; }
    public double   Y         { get; set; }
    public double   Width     { get; set; } = 15;
    public double   Height    { get; set; } = 10;
    public DateTime UpdatedAt { get; set; }
}
