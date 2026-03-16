namespace DEX.Core.Actor

open System
open System.Reactive.Subjects
open Akka.Actor
open Akka.Remote
open DEX.Common.ActorErrors
open Microsoft.Extensions.Logging

[<AbstractClass>]
type DexaGuardianActor(logger: ILogger,
                        server: IActorRef,
                        serverActorPath: string,
                        serverSetter: System.Action<IActorRef>) =
    inherit DexaBaseActor(logger)

    let mutable numFailedRetries = 0
    let mutable scheduleCancelToken: ICancelable = null
    let mutable subscription: AmSubscribe = null
    let mutable currentServer: IActorRef = server

    static member val MonitorActorSubject: Subject<obj> = new Subject<obj>() with get

    member _.IsMyServer(_actor: IActorRef) = true

    member this.ProcServerConnectivity(message: obj) : bool =
        let ctx = UntypedActor.Context
        let sender = this.Sender
        let self = this.Self

        let reschedule () =
            if not (isNull scheduleCancelToken) then
                scheduleCancelToken.Cancel()
            scheduleCancelToken <-
                ctx.System.Scheduler.ScheduleTellRepeatedlyCancelable(
                    TimeSpan.FromMilliseconds(10000.0),
                    TimeSpan.FromMilliseconds(5000.0),
                    self,
                    AmInjectSubscription(subscription),
                    self)

        let onServerShutdown (msg: obj) =
            currentServer <- null
            serverSetter.Invoke(null)
            ctx.Unwatch(sender) |> ignore
            DexaGuardianActor.MonitorActorSubject.OnNext(msg)
            reschedule ()

        match message with
        | :? AmInjectSubscription as injectSubscription ->
            subscription <- injectSubscription.Subscription
            numFailedRetries <- numFailedRetries + 1
            if numFailedRetries > 5 then
                DexaGuardianActor.MonitorActorSubject.OnNext(RepeatedFailToConnect())
                numFailedRetries <- 0
            try
                if isNull currentServer then
                    currentServer <-
                        ctx.ActorSelection(serverActorPath)
                            .ResolveOne(AkkaExt.DefaultAskTimeout).Result
                    serverSetter.Invoke(currentServer)
                if not (isNull currentServer) then
                    currentServer.Tell(subscription)
            with _ ->
                reschedule ()
            true

        | :? AmReplySubscription as replySubscription ->
            ctx.Watch(currentServer) |> ignore
            numFailedRetries <- 0
            if not (isNull scheduleCancelToken) then
                scheduleCancelToken.Cancel()
            DexaGuardianActor.MonitorActorSubject.OnNext(replySubscription)
            true

        | :? Terminated as msg1 ->
            let lower = sender.Path.ToString().ToLower()
            if lower.StartsWith("akka.tcp://client-system") || lower.StartsWith("akka.tcp://agent-system") then
                DexaGuardianActor.MonitorActorSubject.OnNext(PeerTerminated(sender))
                logger.LogWarning(
                    "Peer terminated: {Sender}, AddressTerminated={AddressTerminated}",
                    sender.Path :> obj, msg1.AddressTerminated :> obj)
                ctx.Unwatch(sender) |> ignore
            else
                logger.LogWarning(
                    "Server terminated: {Sender}, AddressTerminated={AddressTerminated}",
                    sender.Path :> obj, msg1.AddressTerminated :> obj)
                onServerShutdown msg1
            true

        | :? AmS2XServerShutdown as msg2 ->
            onServerShutdown msg2
            true

        | :? AssociationErrorEvent as msg3 ->
            onServerShutdown msg3
            true

        | _ -> false
