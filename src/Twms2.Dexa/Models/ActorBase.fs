namespace DEX.Core.Database.ORM

[<AllowNullLiteral>]
[<AbstractClass>]
type ActorBase() =
    member val Ip: string = null with get, set
    member val SwVersion: string = null with get, set
    member this.AssemblyVersion = this.SwVersion
    member val Assemblies: string = null with get, set

    new(swVersion: string, assemblies: string, ipAddress: string) as this =
        ActorBase()
        then
            this.Ip <- ipAddress
            this.SwVersion <- swVersion
            this.Assemblies <- assemblies
