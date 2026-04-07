namespace DEX.Core.Database.ORM

type Schedule() =
    member val Id: int = 0 with get, set
    member val AssetId: int = 0 with get, set
    member val TriggerId: int = 0 with get, set
    member val Deleted: bool = false with get, set

    new(id: int, assetId: int, triggerId: int) as this =
        Schedule()
        then
            this.Id <- id
            this.AssetId <- assetId
            this.TriggerId <- triggerId
