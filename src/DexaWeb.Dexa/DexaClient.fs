namespace DexaWeb.Dexa

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Reactive.Subjects
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open DEX.Common
open DEX.Core.Actor
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// CommProxy 패턴 기반 DEXA Client.
/// 서버의 ShortTerm/LongTerm/Stream actor와 직접 통신.
/// Ask는 SelfShortTermActor를 sender로 Tell + 응답 대기 방식.
type DexaClient(options: IOptions<DexaClientOptions>, logger: ILogger<DexaClient>) =
    let opts = options.Value
    let mutable actorSystem: DexaActorSystem option = None
    let mutable initialized = false
    let mutable disposed = false
    let mutable connectivityTimer: Timer option = None
    let notifications = new Subject<obj>()

    /// 대기 중인 Ask 요청 — 응답 타입별로 TaskCompletionSource 보관
    /// Key: 요청 메시지의 Guid (ActorMessage.Guid)
    let pendingRequests = ConcurrentDictionary<Guid, TaskCompletionSource<obj>>()

    let sendSubscription () =
        if isNull DexaActorSystem.ServerShortTermActor then
            logger.LogWarning("DEXA Server 구독 건너뜀 — 서버 actor가 null")
        else
            let selfActor = DexaActorSystem.SelfShortTermActor
            let ip = EmDns.getLocalIpAddress().ToString()
            let ver =
                let asm = Assembly.GetExecutingAssembly()
                let fvi = FileVersionInfo.GetVersionInfo(asm.Location)
                if isNull fvi.FileVersion then "2.0.0" else fvi.FileVersion
            let clientInfo =
                ClientInfo(
                    selfActor,
                    opts.ClientName,
                    ver,
                    null,
                    ip,
                    Process.GetCurrentProcess().Id)
            DexaActorSystem.ServerShortTermActor.Tell(AmC2SSubscribe(clientInfo), selfActor)
            logger.LogInformation("DEXA Server 구독 요청 전송 (Tell, sender={Sender})", selfActor.Path.ToString() :> obj)

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
                            sendSubscription ()
                            logger.LogInformation("DEXA Server 재연결 및 구독 요청 전송")
                            notifications.OnNext(ConnectivityChangedNotification(true))
                        else
                            logger.LogWarning("DEXA Server 연결 끊김")
                            notifications.OnNext(ConnectivityChangedNotification(false))
                    | None -> ()
            with ex ->
                logger.LogDebug(ex, "Connectivity check 예외")

    /// 서버 응답을 매칭하여 대기 중인 Ask 완료 처리
    let tryCompleteRequest (message: obj) =
        match message with
        | :? AmReply as reply ->
            let query = reply.QueryMessage
            if not (isNull query) then
                let mutable tcs = Unchecked.defaultof<_>
                if pendingRequests.TryRemove(query.Guid, &tcs) then
                    tcs.TrySetResult(message) |> ignore
                    true
                else false
            else false
        | _ -> false

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

                // 서버 응답 수신 → pendingRequests 매칭
                DexaClientShortTermActor.MessageSubject.Subscribe(fun msg ->
                    notifications.OnNext(msg)
                    tryCompleteRequest msg |> ignore
                ) |> ignore

                initialized <- true

                if connected then
                    // 진단: AmPing으로 서버 직렬화 호환성 테스트
                    try
                        let selfActor = DexaActorSystem.SelfShortTermActor
                        let pingMsg = AmPing()
                        logger.LogInformation("진단: AmPing 전송 (guid={Guid})", pingMsg.Guid.ToString().[..7] :> obj)
                        // 1) Tell (selfActor sender) — 서버가 selfActor로 응답
                        DexaActorSystem.ServerShortTermActor.Tell(pingMsg, selfActor)
                        // 2) Ask (temp actor sender) — 서버가 temp actor로 응답
                        try
                            let pongResult =
                                DexaActorSystem.ServerShortTermActor
                                    .Ask<AmPong>(AmPing(), TimeSpan.FromSeconds(5.0))
                                    .GetAwaiter().GetResult()
                            logger.LogInformation("진단: Ask AmPing → AmPong 성공!")
                        with ex ->
                            logger.LogWarning("진단: Ask AmPing 실패: {Err}", ex.Message :> obj)
                    with ex ->
                        logger.LogWarning("진단: Ping 테스트 실패: {Err}", ex.Message :> obj)

                    sendSubscription ()
                    logger.LogInformation(
                        "DEXA Client 초기화 완료 - 서버 연결됨, 구독 요청 전송 (서버: {ServerIp}:{ServerPort})",
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
            let selfActor = DexaActorSystem.SelfShortTermActor
            if not initialized || isNull serverActor then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))

            let actualTimeout = defaultArg timeout (TimeSpan.FromSeconds(float opts.AskTimeoutSeconds))

            // ActorMessage에서 Guid 추출 (응답 매칭용)
            let guid =
                match message with
                | :? ActorMessage as am -> am.Guid
                | _ -> Guid.NewGuid()

            let tcs = TaskCompletionSource<obj>(TaskCreationOptions.RunContinuationsAsynchronously)
            pendingRequests.[guid] <- tcs

            logger.LogInformation(
                "Tell 전송 (Ask 대체): {MsgType} → {ActorPath} (guid={Guid}, timeout: {Timeout}s)",
                message.GetType().Name :> obj,
                serverActor.Path.ToString() :> obj,
                guid.ToString().[..7] :> obj,
                actualTimeout.TotalSeconds :> obj)

            // SelfShortTermActor를 sender로 Tell — 서버가 구독된 클라이언트로 인식
            serverActor.Tell(message, selfActor)

            // 타임아웃 처리
            let cts = new CancellationTokenSource(actualTimeout)
            cts.Token.Register(fun () ->
                let mutable removed = Unchecked.defaultof<_>
                if pendingRequests.TryRemove(guid, &removed) then
                    removed.TrySetException(
                        AskTimeoutException(sprintf "Timeout after %O seconds" actualTimeout)) |> ignore
            ) |> ignore

            task {
                let! result = tcs.Task
                cts.Dispose()
                return result :?> 'T
            }

        member _.TellServer(message: obj) =
            let serverActor = DexaActorSystem.ServerShortTermActor
            let selfActor = DexaActorSystem.SelfShortTermActor
            if not initialized || isNull serverActor then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))
            serverActor.Tell(message, selfActor)

        member _.ServerNotifications = notifications :> IObservable<obj>

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                match connectivityTimer with
                | Some t -> t.Dispose()
                | None -> ()
                // 대기 중인 요청 모두 취소
                for kvp in pendingRequests do
                    kvp.Value.TrySetCanceled() |> ignore
                pendingRequests.Clear()
                notifications.OnCompleted()
                notifications.Dispose()
                match actorSystem with
                | Some s -> (s :> IDisposable).Dispose()
                | None -> ()
                initialized <- false
