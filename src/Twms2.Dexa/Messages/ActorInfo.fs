namespace DEX.Core.Actor

open Akka.Actor
open DEX.Common

[<AbstractClass; AllowNullLiteral>]
type ActorInfo() =
    inherit DEX.Core.Database.ORM.ActorBase()

    member val ActorRef: IActorRef = null with get, set
    member val Name: string = null with get, set
    member val ActorType: ActorType = ActorType.Client with get, set
    member val Pid: int = 0 with get, set

    new(name: string,
        actor: IActorRef,
        actorType: ActorType,
        swVersion: string,
        assemblies: string,
        ipAddress: string,
        processId: int) as this =
        ActorInfo()
        then
            this.Ip <- ipAddress
            this.SwVersion <- swVersion
            this.Assemblies <- assemblies
            this.Name <- name
            this.ActorRef <- actor
            this.ActorType <- actorType
            this.Pid <- processId

    override this.ToString() =
        sprintf "%s %s@%O" (this.GetType().Name) this.Name this.ActorType
