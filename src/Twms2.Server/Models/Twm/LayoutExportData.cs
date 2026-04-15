namespace Twms2.Server.Models.Twm;

/// <summary>레이아웃 JSON 내보내기/가져오기 데이터 구조.</summary>
public class LayoutExportData
{
    public int Version { get; set; } = 1;
    public string? LayoutName { get; set; }
    public DateTime ExportedAt { get; set; }

    public LayoutExportConfig? Config { get; set; }
    public List<LayoutExportRect> BlueprintRects { get; set; } = [];
    public List<LayoutExportPosition> Positions { get; set; } = [];
    public List<LayoutExportGroup> Groups { get; set; } = [];
}

public class LayoutExportConfig
{
    public string? BgColor { get; set; }
    public string? GridColor { get; set; }
    public double? ImageWidth { get; set; }
    public double? ImageHeight { get; set; }
}

public class LayoutExportRect
{
    public int LineId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class LayoutExportPosition
{
    public int AssetId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; }
    public bool Visible { get; set; }
}

public class LayoutExportGroup
{
    public string? Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? Color { get; set; }
    public List<int> MemberAssetIds { get; set; } = [];
}
