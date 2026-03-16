namespace DexaWeb.Dexa

open System
open System.Diagnostics
open System.Reactive.Subjects
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open DEX.Common
open DEX.Core.Actor
open DEX.Core.Database.ORM
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

type DexaClient(options: IOptions<DexaClientOptions>, logger: ILogger<DexaClient>) =
    let opts = options.Value
    let mutable actorSystem: DexaActorSystem option = None
    let mutable initialized = false
    let mutable disposed = false
    let mutable connectivityTimer: Timer option = None
    let mutable subscriptionMessage: AmInjectSubscription option = None
    let notifications = new Subject<obj>()

    let createSubscriptionMessage () =
        let ipAddress = EmDns.getLocalIpAddress().ToString()
        let swVersion = "1.0.0"
        let assemblies = sprintf "DexaWeb.Dexa\t%s\t%O" swVersion DateTime.Now
        let pid = Process.GetCurrentProcess().Id

        let clientInfo =
            ClientInfo(
                DexaActorSystem.SelfGuardianActor,
                opts.ClientName,
                swVersion,
                assemblies,
                ipAddress,
                pid)

        AmInjectSubscription(AmC2SSubscribe(clientInfo))

    let checkConnectivity (_state: obj) =
        if not initialized || disposed then ()
        else
            try
                if isNull DexaActorSystem.TheServerActor then
                    logger.LogInformation("DEXA Server 재연결 시도 중...")
                    let msg =
                        match subscriptionMessage with
                        | Some m -> m
                        | None -> createSubscriptionMessage ()
                    if not (isNull DexaActorSystem.SelfGuardianActor) then
                        DexaActorSystem.SelfGuardianActor.Tell(msg)
            with ex ->
                logger.LogDebug(ex, "Connectivity check 예외")

    interface IDexaClient with
        member _.IsConnected =
            initialized && not (isNull DexaActorSystem.TheServerActor)

        member _.PingServerAsync() =
            Task.FromResult(initialized && not (isNull DexaActorSystem.TheServerActor))

        member _.InitializeAsync() =
            try
                let system = new DexaActorSystem(logger, opts)
                actorSystem <- Some system

                DexaActorSystem.SelfGuardianActor <-
                    system.ActorSystem.ActorOf(
                        Props.Create(fun () -> DexaClientGuardian(system)),
                        DEXActorGlobal.GuardianActorName)

                system.ConnectToServer()

                let msg = createSubscriptionMessage ()
                subscriptionMessage <- Some msg
                DexaActorSystem.SelfGuardianActor.Tell(msg)

                DexaClientGuardian.DataChanged.Subscribe(fun dc -> notifications.OnNext(dc)) |> ignore
                DexaClientGuardian.MessageSubject.Subscribe(fun msg -> notifications.OnNext(msg)) |> ignore

                DexaGuardianActor.MonitorActorSubject.Subscribe(fun msg ->
                    match msg with
                    | :? AmReplySubscription ->
                        logger.LogInformation("DEXA Server 연결됨")
                        notifications.OnNext(ConnectivityChangedNotification(true))
                    | :? Terminated
                    | :? AmS2XServerShutdown
                    | :? DEX.Common.ActorErrors.RepeatedFailToConnect ->
                        logger.LogWarning("DEXA Server 연결 끊김")
                        notifications.OnNext(ConnectivityChangedNotification(false))
                    | _ -> ()
                ) |> ignore

                initialized <- true
                logger.LogInformation(
                    "DEXA Client 초기화 완료 (서버: {ServerIp}:{ServerPort})",
                    opts.ServerIp, opts.ServerPort)

                connectivityTimer <-
                    Some(new Timer(
                        TimerCallback(checkConnectivity),
                        null,
                        TimeSpan.FromSeconds(10.0),
                        TimeSpan.FromSeconds(10.0)))
            with ex ->
                initialized <- false
                logger.LogError(ex, "DEXA Client 초기화 실패")

            Task.CompletedTask

        member _.AskServerAsync<'T>(message: obj, ?timeout: TimeSpan) =
            if not initialized || isNull DexaActorSystem.TheServerActor then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))
            let actualTimeout = defaultArg timeout (TimeSpan.FromSeconds(float opts.AskTimeoutSeconds))
            DexaActorSystem.TheServerActor.Ask<'T>(message, actualTimeout)

        member _.TellServer(message: obj) =
            if not initialized || isNull DexaActorSystem.TheServerActor then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))
            DexaActorSystem.TheServerActor.Tell(message)

        member _.ServerNotifications = notifications :> IObservable<obj>

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                match connectivityTimer with
                | Some t -> t.Dispose()
                | None -> ()
                notifications.OnCompleted()
                notifications.Dispose()
                match actorSystem with
                | Some s -> (s :> IDisposable).Dispose()
                | None -> ()
                initialized <- false
