namespace DEX.Core.Actor

open System
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Akka.Actor
open Microsoft.Extensions.Logging

[<Extension>]
type AkkaExt =
    static member val DefaultAskTimeout = TimeSpan.FromSeconds(10.0) with get, set
    static member val DefaultPingTimeout = TimeSpan.FromSeconds(2.0) with get, set

    [<Extension>]
    static member GetServerActor(actorSystem: ActorSystem, actorPath: string, logger: ILogger) =
        try
            actorSystem.ActorSelection(actorPath).ResolveOne(AkkaExt.DefaultAskTimeout).Result
        with ex ->
            if ex.ToString().Contains("Akka.Actor.ActorNotFoundException") then
                if not (isNull logger) then
                    logger.LogWarning("Actor를 찾을 수 없습니다: {ActorPath}", actorPath)
            else
                if not (isNull logger) then
                    logger.LogError(ex, "Actor 연결 실패: {ActorPath}", actorPath)
            reraise ()

    [<Extension>]
    static member GetServerActorAsync(actorSystem: ActorSystem, actorPath: string, logger: ILogger) =
        task {
            try
                return! actorSystem.ActorSelection(actorPath).ResolveOne(AkkaExt.DefaultAskTimeout)
            with ex ->
                if ex.ToString().Contains("Akka.Actor.ActorNotFoundException") then
                    if not (isNull logger) then
                        logger.LogWarning("Actor를 찾을 수 없습니다: {ActorPath}", actorPath)
                else
                    if not (isNull logger) then
                        logger.LogError(ex, "Actor 연결 실패: {ActorPath}", actorPath)
                return raise ex
        }

    [<Extension>]
    static member GetIdentityAsync(actor: ICanTell, timeout: TimeSpan) : Task<ActorIdentity> =
        task {
            try
                return! actor.Ask<ActorIdentity>(Identify(Guid.NewGuid()), timeout)
            with _ ->
                return null
        }

    [<Extension>]
    static member PingAsync(actor: ICanTell, timeout: TimeSpan) : Task<bool> =
        task {
            let! identity = AkkaExt.GetIdentityAsync(actor, timeout)
            return not (isNull identity)
        }

    [<Extension>]
    static member AskLimited<'T>(canTell: ICanTell, message: obj) : Task<'T> =
        canTell.Ask<'T>(message, AkkaExt.DefaultAskTimeout)

    [<Extension>]
    static member AskLimited<'T>(canTell: ICanTell, message: obj, timeout: Nullable<TimeSpan>) : Task<'T> =
        canTell.Ask<'T>(message, timeout)

    [<Extension>]
    static member GetParentPathName(actor: IActorRef) : string =
        if isNull actor then null
        else
            let parent = actor.Path.Parent
            if isNull parent then null
            else parent.Address.ToString()

    [<Extension>]
    static member IsSameHost(a: IActorRef, b: IActorRef) =
        not (isNull a) && not (isNull b) && AkkaExt.GetHost(a) = AkkaExt.GetHost(b)

    [<Extension>]
    static member IsSameActor(a: IActorRef, b: IActorRef) =
        if isNull a || isNull b then false
        elif obj.ReferenceEquals(a, b) then true
        else AkkaExt.GetHost(a) = AkkaExt.GetHost(b) && AkkaExt.GetPort(a) = AkkaExt.GetPort(b)

    [<Extension>]
    static member GetPort(actor: IActorRef) : Nullable<int> =
        actor.Path.Address.Port

    [<Extension>]
    static member GetHost(actor: IActorRef) : string =
        actor.Path.Address.Host
