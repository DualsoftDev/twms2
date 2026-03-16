namespace DexaWeb.Server.Models.Dexa;

/// <summary>
/// 일괄 수정 건별 결과.
/// </summary>
public class BatchUpdateResult
{
    public int AssetId { get; init; }
    public string AssetName { get; init; } = "";
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}
