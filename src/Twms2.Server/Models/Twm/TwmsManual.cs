namespace Twms2.Server.Models.Twm;

/// <summary>매뉴얼 관리 (키워드 기반 PDF 매뉴얼).</summary>
public class TwmsManual
{
    public int Id { get; set; }
    public string Keyword { get; set; } = "";
    public string FileName { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}
