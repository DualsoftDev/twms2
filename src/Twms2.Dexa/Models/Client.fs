namespace DEX.Core.Database.ORM

open System

type Client() =
    member val Id: int = 0 with get, set
    member val Name: string = null with get, set
    member val SwVersion: string = null with get, set
    member val Assemblies: string = null with get, set
    member val Ip: string = null with get, set
    member val Online: bool = false with get, set
    member val Connected: DateTime = DateTime.MinValue with get, set
    member val Disconnected: Nullable<DateTime> = Nullable<DateTime>() with get, set
