namespace Twms2.Server.Models.Twm;

/// <summary>도면 설정 (레이아웃별). DrawingData에 벡터 도형 JSON 저장.</summary>
public class TwmsBlueprintConfig
{
    public int      LayoutId    { get; set; }
    public string?  ImagePath   { get; set; }
    public double?  ImageWidth  { get; set; }
    public double?  ImageHeight { get; set; }
    public string?  DrawingData { get; set; }
    public string?  BgColor     { get; set; }
    public string?  GridColor   { get; set; }
    public DateTime UpdatedAt   { get; set; }
}
