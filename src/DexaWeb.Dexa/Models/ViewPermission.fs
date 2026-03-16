namespace DEX.Core.Database.ORM

type ViewPermission() =
    inherit Permission()

    member val AssetParameter: string = null with get, set
    member val UserName: string = null with get, set
    member val IsAdmin: bool = false with get, set

    new(id: int, uid: int, assetId: int) as this =
        ViewPermission()
        then
            this.Id <- id
            this.Uid <- uid
            this.AssetId <- assetId
