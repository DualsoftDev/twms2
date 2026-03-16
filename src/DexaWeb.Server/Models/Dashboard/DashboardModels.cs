namespace DexaWeb.Server.Models.Dashboard;

/// <summary>백업 결과 상태</summary>
public enum AssetHealthStatus
{
    /// <summary>백업 갱신 (백업 성공 + 내용 변경)</summary>
    BackedUp,
    /// <summary>변경 없음 (백업 성공 + 내용 동일)</summary>
    Unchanged,
    /// <summary>작업 실패</summary>
    Failed,
    /// <summary>작업중 (시작됨 + 미종료 + Memo 공란 + 5시간 미만)</summary>
    InProgress,
    /// <summary>내역 없음 (백업 이력 없음)</summary>
    Unknown,
}

/// <summary>자산 상태 정보 (DEXA + TWM 통합)</summary>
public class AssetStatusInfo
{
    public int AssetId { get; set; }
    public string Name { get; set; } = "";
    public string? Ip { get; set; }
    public string? AssetTypeName { get; set; }
    public int? AssetTypeId { get; set; }
    public bool AgentOnline { get; set; }
    public string? AgentName { get; set; }
    public DateTime? LastBackupTime { get; set; }
    public bool LastBackupSucceeded { get; set; }
    public bool? LastBackupChanged { get; set; }
    public string? GroupName { get; set; }
    public List<string> Tags { get; set; } = [];
    public PingStatus? LatestPing { get; set; }

    // Aug 확장 필드 (그룹핑용)
    public string? AugIpVia { get; set; }
    public int? AugLineId { get; set; }
    public int? AugIsRobotPLC { get; set; }
    public string? LayoutLineName { get; set; }

    // 자산 통합조회용 확장 필드
    public string? Description { get; set; }
    public string? ModelVersion { get; set; }
    public int? AugStationNumber { get; set; }
    public string? AugVendor { get; set; }
    public string? AugSpec { get; set; }
    public int? AugBaseNumber { get; set; }
    public int? AugSlotNumber { get; set; }

    public AssetHealthStatus Health { get; set; } = AssetHealthStatus.Unknown;
    /// <summary>최근 백업이 진행중인지 (Finished null + Memo 공란)</summary>
    public bool LastBackupInProgress { get; set; }
    /// <summary>최근 연속 실패 횟수 (성공 만나면 리셋)</summary>
    public int ConsecutiveFailureCount { get; set; }
}

/// <summary>Ping 결과</summary>
public class PingStatus
{
    public bool Reachable { get; set; }
    public int? RoundtripMs { get; set; }
    public DateTime CheckedAt { get; set; }
}

/// <summary>백업 요약 통계</summary>
public class BackupSummary
{
    public int TotalAssets { get; set; }
    public int BackedUpToday { get; set; }
    public int FailedToday { get; set; }
    public double SuccessRate7d { get; set; }
}

/// <summary>에이전트 상태 요약</summary>
public class AgentSummary
{
    public int OnlineCount { get; set; }
    public int OfflineCount { get; set; }
    public int TotalCount { get; set; }
    public List<AgentInfo> Agents { get; set; } = [];
}

/// <summary>에이전트 정보</summary>
public class AgentInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Ip { get; set; }
    public bool Online { get; set; }
    public string? SwVersion { get; set; }
    public DateTime? Connected { get; set; }
    public DateTime? Disconnected { get; set; }
}

/// <summary>Ping 요약 통계</summary>
public class PingSummary
{
    public int ReachableCount { get; set; }
    public int UnreachableCount { get; set; }
    public int UnknownCount { get; set; }
}

/// <summary>드라이브 용량 정보</summary>
public class DriveStat
{
    public string DriveName { get; set; } = "";
    public string DbFilePath { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPct => TotalBytes > 0 ? 100.0 * UsedBytes / TotalBytes : 0;
}

/// <summary>당일 백업 스케줄 항목 (타임라인 표시용)</summary>
public class ScheduleEntry
{
    public int AssetId { get; set; }
    public string AssetName { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime? Finished { get; set; }
    public bool InProgress { get; set; }
    /// <summary>null=진행중, true=성공, false=실패</summary>
    public bool? Success { get; set; }
}

/// <summary>최근 활동 항목</summary>
public class RecentActivity
{
    public string Source { get; set; } = "";   // "dexa" or "twm"
    public int? AssetId { get; set; }
    public string? AssetName { get; set; }
    public string Action { get; set; } = "";
    public bool? Success { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>대시보드 전체 데이터</summary>
public class DashboardData
{
    public bool DexaConnected { get; set; }
    public BackupSummary Backup { get; set; } = new();
    public AgentSummary Agents { get; set; } = new();
    public PingSummary Ping { get; set; } = new();
    public List<AssetStatusInfo> Assets { get; set; } = [];
    public List<RecentActivity> RecentActivities { get; set; } = [];
}
