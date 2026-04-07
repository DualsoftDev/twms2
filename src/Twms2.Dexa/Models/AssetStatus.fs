namespace DEX.Core.Database.ORM

open System

type AssetStatus() =
    member val AssetId: int = 0 with get, set
    member val Available: int = 0 with get, set
    member val Time: Nullable<DateTime> = Nullable<DateTime>() with get, set
    member val Status: string = null with get, set
