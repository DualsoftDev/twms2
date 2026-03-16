namespace DEX.Core.Actor

open Akka.Actor

[<AllowNullLiteral>]
type Locker(actor: IActorRef, ip: string, user: string) =
    member val Actor: IActorRef = actor with get, set
    member val Ip: string = ip with get, set
    member val User: string = user with get, set
