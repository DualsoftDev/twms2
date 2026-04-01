namespace DEX.Core.Database.ORM

open System

[<AllowNullLiteral>]
type Action() =
    member val Id: int = 0 with get, set
    member val AssetId: int = 0 with get, set
    member val AgentId: int = 0 with get, set
    member val ScheduleId: Nullable<int> = Nullable<int>() with get, set
    member val TrialId: int = 0 with get, set
    member val Version: Nullable<int> = Nullable<int>() with get, set
    member val Started: DateTime = DateTime.MinValue with get, set
    member val Finished: Nullable<DateTime> = Nullable<DateTime>() with get, set
    member val NthSucceeded: Nullable<int> = Nullable<int>() with get, set

    member this.Succeeded =
        this.NthSucceeded.HasValue && this.NthSucceeded.Value >= 0
        && not (String.Equals(this.Memo, "false", StringComparison.OrdinalIgnoreCase))

    member val ContentsChanged: bool = false with get, set
    member val Memo: string = null with get, set
    member val Exception: string = null with get, set
