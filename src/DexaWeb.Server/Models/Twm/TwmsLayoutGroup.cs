namespace DexaWeb.Server.Models.Twm;

/// <summary>DEXA layoutGroup 가져오기 로컬 복사본.</summary>
public class TwmsLayoutGroup
{
    public int     Id       { get; set; }
    public int     AssetId  { get; set; }
    public int?    Floor    { get; set; }
    public string? Assets   { get; set; }  // JSON: assetId 목록
    public DateTime UpdatedAt { get; set; }
}
