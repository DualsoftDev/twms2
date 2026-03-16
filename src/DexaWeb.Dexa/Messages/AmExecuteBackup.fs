namespace DEX.Core.Actor

[<AbstractClass>]
type AmExecuteBackup(assetId: int, scheduleId: int option) =
    inherit ActorMessage()
    member val AssetId: int = assetId with get, set
    member val ScheduleId: int option = scheduleId with get, set

    new() = AmExecuteBackup(0, None)
