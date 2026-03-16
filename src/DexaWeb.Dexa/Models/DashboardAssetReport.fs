namespace DEX.Core.Database.ORM

open System
open System.Collections.Generic

[<AllowNullLiteral>]
type DashboardAssetReport() =
    member val TotalNumVersions: int = 0 with get, set
    member val TotalNumTrials: int = 0 with get, set
    member val Agent: Agent = null with get, set
    member val Asset: Asset = Asset() with get, set
    member val LastSucceededAction: Action = null with get, set
    member val LastAction: Action = null with get, set
    member val Actions: List<Action> = List<Action>() with get, set
    member val LastVersion: Nullable<int> = Nullable<int>() with get, set
    member val LastSucceededVersion: Nullable<int> = Nullable<int>() with get, set
