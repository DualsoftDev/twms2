namespace DexaWeb.Server.Models.Dexa;

public class DexaAction
{
    /// <summary>진행 중 백업이 이 시간을 초과하면 미완료(실패)로 간주</summary>
    public static readonly TimeSpan IncompleteThreshold = TimeSpan.FromHours(5);

    public int Id { get; set; }
    public int AssetId { get; set; }
    public int? Version { get; set; }
    public DateTime? Started { get; set; }
    public DateTime? Finished { get; set; }
    public bool? ContentsChanged { get; set; }
    public string? Memo { get; set; }

    /// <summary>백업 성공 여부 (Memo가 "true"이면 성공)</summary>
    public bool IsSuccess => Memo?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>진행 중 여부 (미완료 + Memo 없음)</summary>
    public bool IsInProgress => Finished == null && string.IsNullOrEmpty(Memo);

    /// <summary>미완료 판정 (진행 중 + IncompleteThreshold 초과)</summary>
    public bool IsIncomplete => IsInProgress
        && Started.HasValue
        && (DateTime.Now - Started.Value) >= IncompleteThreshold;
}
