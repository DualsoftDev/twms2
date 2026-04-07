namespace DEX.Core.Database.ORM

open System

type BackupLog() =
    member val Id: int = 0 with get, set
    member val ActionId: int = 0 with get, set
    member val Level: string = null with get, set
    member val Message: string = null with get, set
    member val DateTime: DateTime = DateTime.MinValue with get, set
    member val Thread: int = 0 with get, set
