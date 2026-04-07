namespace DEX.Common.ActorErrors

open Akka.Actor

type PeerTerminated(actor: IActorRef) =
    member _.Actor = actor

type Quarantined() =
    class
    end

type RepeatedFailToConnect() =
    class
    end
