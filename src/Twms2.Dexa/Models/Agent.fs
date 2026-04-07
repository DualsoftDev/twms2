namespace DEX.Core.Database.ORM

open System

[<AllowNullLiteral>]
type Agent() =
    inherit ActorBase()

    member val Id: int = 0 with get, set
    member val Name: string = null with get, set
    member val Online: bool = false with get, set
    member val Connected: DateTime = DateTime.MinValue with get, set
    member val Disconnected: Nullable<DateTime> = Nullable<DateTime>() with get, set

    new(id: int,
        name: string,
        swVersion: string,
        assemblies: string,
        ip: string,
        online: bool,
        connected: DateTime,
        ?disconnected: Nullable<DateTime>) as this =
        Agent()
        then
            this.Ip <- ip
            this.SwVersion <- swVersion
            this.Assemblies <- assemblies
            this.Id <- id
            this.Name <- name
            this.Online <- online
            this.Connected <- connected
            this.Disconnected <- defaultArg disconnected (Nullable<DateTime>())
