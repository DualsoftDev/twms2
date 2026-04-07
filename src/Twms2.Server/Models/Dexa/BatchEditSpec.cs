namespace Twms2.Server.Models.Dexa;

/// <summary>
/// 일괄 수정 시 변경할 필드 명세.
/// null인 필드는 변경하지 않음 (체크박스로 제어).
/// 모든 필드는 DEXA parameter(HOCON)에 기록.
/// </summary>
public class BatchEditSpec
{
    // ── DEXA parameter (HOCON) 필드 ──

    /// <summary>에이전트 선호도 (세미콜론 구분). null = 변경 안 함.</summary>
    public string? AgentPreferences { get; set; }

    /// <summary>HOCON .name.value 필드. null = 변경 안 함.</summary>
    public string? Name { get; set; }

    /// <summary>HOCON .IP.value 필드. null = 변경 안 함.</summary>
    public string? Ip { get; set; }

    /// <summary>HOCON .description.value 필드. null = 변경 안 함.</summary>
    public string? Description { get; set; }

    /// <summary>HOCON .via1_connection.value (경유 IP). null = 변경 안 함, "" = 클리어.</summary>
    public string? ViaIp { get; set; }

    /// <summary>HOCON .via1_base.value (Base 번호). null = 변경 안 함.</summary>
    public int? BaseNumber { get; set; }

    /// <summary>HOCON .via1_slot.value (Slot 번호). null = 변경 안 함.</summary>
    public int? SlotNumber { get; set; }

    public bool HasChanges =>
        AgentPreferences != null || Name != null || Ip != null || Description != null
        || ViaIp != null || BaseNumber != null || SlotNumber != null;
}
