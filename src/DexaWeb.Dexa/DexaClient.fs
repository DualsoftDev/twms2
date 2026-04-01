namespace DexaWeb.Dexa

open System
open System.Reactive.Subjects
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open DEX.Core.Actor
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// CommProxy 패턴 기반 DEXA Client.
/// 서버의 ShortTerm/LongTerm/Stream actor와 직접 통신.
type DexaClient(options: IOptions<DexaClientOptions>, logger: ILogger<DexaClient>) =
    let opts = options.Value
    let mutable actorSystem: DexaActorSystem option = None
    let mutable initialized = false
    let mutable disposed = false
    let mutable connectivityTimer: Timer option = None
    let notifications = new Subject<obj>()

    let checkConnectivity (_state: obj) =
        if not initialized || disposed then ()
        else
            try
                if not (isNull DexaActorSystem.ServerShortTermActor) then ()
                else
                    logger.LogInformation("DEXA Server 재연결 시도 중...")
                    match actorSystem with
                    | Some system ->
                        if system.ConnectToServer() then
                            logger.LogInformation("DEXA Server 연결됨")
                            notifications.OnNext(ConnectivityChangedNotification(true))
                        else
                            logger.LogWarning("DEXA Server 연결 끊김")
                            notifications.OnNext(ConnectivityChangedNotification(false))
                    | None -> ()
            with ex ->
                logger.LogDebug(ex, "Connectivity check 예외")

    interface IDexaClient with
        member _.IsConnected =
            initialized && not (isNull DexaActorSystem.ServerShortTermActor)

        member _.PingServerAsync() =
            Task.FromResult(initialized && not (isNull DexaActorSystem.ServerShortTermActor))

        member _.InitializeAsync() =
            try
                let system = new DexaActorSystem(logger, opts)
                actorSystem <- Some system

                // 로컬 ShortTerm actor 생성 (서버 알림 수신용)
                let actorName = opts.ClientName + "-ShortTerm"
                DexaActorSystem.SelfShortTermActor <-
                    system.ActorSystem.ActorOf(
                        Props.Create(fun () -> DexaClientShortTermActor(logger)),
                        actorName)
                logger.LogInformation("로컬 ShortTerm actor 생성: {Name}", actorName :> obj)

                // 서버 ShortTerm/LongTerm/Stream actor resolve
                let connected = system.ConnectToServer()

                // 알림 구독
                DexaClientShortTermActor.DataChanged.Subscribe(fun dc ->
                    notifications.OnNext(dc)) |> ignore

                DexaClientShortTermActor.MessageSubject.Subscribe(fun msg ->
                    notifications.OnNext(msg)) |> ignore

                initialized <- true

                if connected then
                    logger.LogInformation(
                        "DEXA Client 초기화 완료 - 서버 연결됨 (서버: {ServerIp}:{ServerPort})",
                        opts.ServerIp :> obj, opts.ServerPort :> obj)
                    notifications.OnNext(ConnectivityChangedNotification(true))
                else
                    logger.LogWarning(
                        "DEXA Client 초기화 완료 - 서버 미연결 (서버: {ServerIp}:{ServerPort}). 재연결 시도 예정.",
                        opts.ServerIp :> obj, opts.ServerPort :> obj)

                // 주기적 연결 체크 타이머
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
            let serverActor = DexaActorSystem.ServerShortTermActor
            if not initialized || isNull serverActor then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))
            let actualTimeout = defaultArg timeout (TimeSpan.FromSeconds(float opts.AskTimeoutSeconds))
            serverActor.Ask<'T>(message, actualTimeout)

        member _.TellServer(message: obj) =
            let serverActor = DexaActorSystem.ServerShortTermActor
            if not initialized || isNull serverActor then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))
            serverActor.Tell(message)

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
