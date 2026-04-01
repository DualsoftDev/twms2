namespace DEX.Core.Database.ORM

type ViewSchedule() =
    member val ScheduleId: int = 0 with get, set
    member val TriggerId: int = 0 with get, set
    member val TriggerName: string = null with get, set
    member val TriggerCronSpec: string = null with get, set
    member val TriggerEnable: bool = false with get, set
    member val TriggerDeleted: bool = false with get, set
    member val AssetId: int = 0 with get, set
    member val AssetParentId: int = 0 with get, set
    member val AssetTypeId: int = 0 with get, set
    member val AssetStorageId: int = 0 with get, set
    member val AssetParameter: string = null with get, set
    member val AssetDescription: string = null with get, set
    member val AssetDeleted: bool = false with get, set
    member val ScheduleDeleted: bool = false with get, set
    member val ConnectionString: string = null with get, set

    member this.GetAssetName() : string =
        this.AssetParameter
