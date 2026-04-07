namespace DEX.Common.Core

open System

[<AllowNullLiteral>]
type HardwarePerformance() =
    member val ClockSpeedMHz: uint32 = 0u with get, set
    member val CoreUsages: float32 array = null with get, set
    member val RAMTotalMB: int64 = 0L with get, set
    member val RAMAvailableMB: int64 = 0L with get, set
    member val TotalScore: int = 0 with get, set
    member val TimeStamp: DateTime = DateTime.MinValue with get, set
