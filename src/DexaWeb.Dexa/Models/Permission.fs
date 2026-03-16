namespace DEX.Core.Database.ORM

type Permission() =
    member val Id: int = 0 with get, set
    member val Uid: int = 0 with get, set
    member val AssetId: int = 0 with get, set

    new(id: int, uid: int, assetId: int) as this =
        Permission()
        then
            this.Id <- id
            this.Uid <- uid
            this.AssetId <- assetId
