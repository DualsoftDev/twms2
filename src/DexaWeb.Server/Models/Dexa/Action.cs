namespace DexaWeb.Server.Models.Dexa;

public class DexaAction
{
    /// <summary>진행 중 백업이 이 시간을 초과하면 미완료(실패)로 간주</summary>
    public static readonly TimeSpan IncompleteThreshold = TimeSpan.FromHours(5);

    public int Id { get; set; }
    public int AssetId { get; set; }
    public int AgentId { get; set; }
    public int? ScheduleId { get; set; }
    public int? Version { get; set; }
    public DateTime? Started { get; set; }
    public DateTime? Finished { get; set; }
    public bool? ContentsChanged { get; set; }

    /// <summary>
    /// DEXA 2.20: null이면 fail, 음수(-1)이면 아직 결과 미수신, 0이면 실패(0개 성공), >= 1이면 성공.
    /// </summary>
    public int? NthSucceeded { get; set; }

    public string? Memo { get; set; }
    public string? Exception { get; set; }

    /// <summary>백업 성공 여부 (nthSucceeded >= 1이고 memo가 "false"가 아니면 성공)</summary>
    public bool IsSuccess => NthSucceeded.HasValue && NthSucceeded.Value > 0
        && !string.Equals(Memo, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>진행 중 여부 (Started 있고 Finished 없으면 진행 중)</summary>
    public bool IsInProgress => Started.HasValue && Finished == null;

    /// <summary>미완료 판정 (진행 중 + IncompleteThreshold 초과)</summary>
    public bool IsIncomplete => IsInProgress
        && Started.HasValue
        && (DateTime.Now - Started.Value) >= IncompleteThreshold;

    /// <summary>실패 시 같은 자산의 마지막 성공 버전 (후처리로 채워짐, DB 컬럼 아님)</summary>
    public int? LastSuccessVersion { get; set; }

    /// <summary>다운로드 가능 버전 (성공 시 자신의 Version, 실패 시 LastSuccessVersion)</summary>
    public int? DownloadableVersion => Version ?? LastSuccessVersion;
}
