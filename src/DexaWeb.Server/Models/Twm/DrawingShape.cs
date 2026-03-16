namespace DexaWeb.Server.Models.Twm;

/// <summary>도면 벡터 도형 (JSON으로 TwmsBlueprintConfig.DrawingData에 저장).</summary>
public class DrawingShape
{
    public string Id   { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Type { get; set; } = "rect"; // rect, line, circle, text

    // 공통 좌표 (SVG viewBox 0 0 1000 600 기준)
    public double X  { get; set; }
    public double Y  { get; set; }
    public double W  { get; set; }  // rect 너비
    public double H  { get; set; }  // rect 높이
    public double X2 { get; set; }  // line 끝점
    public double Y2 { get; set; }  // line 끝점
    public double R  { get; set; }  // circle 반지름

    public string? Text { get; set; }

    // 스타일
    public string  Stroke      { get; set; } = "#333333";
    public double  StrokeWidth { get; set; } = 2;
    public string? Fill        { get; set; }
}
