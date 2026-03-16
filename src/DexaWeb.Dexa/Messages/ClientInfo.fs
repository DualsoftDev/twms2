namespace DEX.Core.Actor

open System
open Akka.Actor
open DEX.Common

type ClientInfo(actor: IActorRef,
                name: string,
                swVersion: string,
                assemblies: string,
                ipAddress: string,
                processId: int) =
    inherit ActorInfo(name, actor, ActorType.Client, swVersion, assemblies, ipAddress, processId)

    member val TableId: Nullable<int> = Nullable() with get, set

    internal new() = ClientInfo(null, null, null, null, null, 0)
