namespace DEX.Core.Actor

open System
open System.Reactive.Disposables
open System.Reactive.Subjects
open System.Runtime.CompilerServices
open Akka.Actor
open Akka.Configuration
open Akka.Remote
open DexaWeb.Dexa
open DexaWeb.Dexa.Infrastructure
open Microsoft.Extensions.Logging

/// CommProxy 패턴 기반 DexaActorSystem.
/// 서버의 ShortTerm/LongTerm/Stream actor를 직접 resolve하여 통신.
type DexaActorSystem(logger: ILogger, options: DexaClientOptions) =

    let disposables = new CompositeDisposable()
    let mutable actorSystem: ActorSystem = null

    let clientName = options.ClientName

    // 서버 actor 경로 (DEXServer-ShortTerm, DEXServer-LongTerm, DEXServer-Stream)
    // Akka actor path는 대소문자 구분 - 서버의 public-hostname은 소문자
    let serverBase =
        sprintf "akka.tcp://%s@%s:%d/user/%s"
            DEXActorGlobal.BackupServerActorSystemName
            (options.ServerIp.ToLowerInvariant())
            options.ServerPort
            DEXActorGlobal.ServerName

    let serverShortTermPath = serverBase + "-ShortTerm"
    let serverLongTermPath  = serverBase + "-LongTerm"
    let serverStreamPath    = serverBase + "-Stream"

    // Akka.Remote 어셈블리가 로드되어야 reference.conf가 적용됨
    static do ignore typeof<RemoteSettings>

    do
        DexaAssemblyResolver.Register()
        AkkaExt.DefaultAskTimeout <- TimeSpan.FromSeconds(float options.AskTimeoutSeconds)
        AkkaExt.DefaultPingTimeout <- TimeSpan.FromSeconds(float options.PingTimeoutSeconds)

        let hostname = Environment.MachineName

        // DEXCommProxy.Initialize와 동일한 방식: 전체 HOCON을 직접 제공 (fallback 미사용)
        let hoconParts = ResizeArray<string>()
        hoconParts.Add("akka.actor.provider = \"Akka.Remote.RemoteActorRefProvider, Akka.Remote\"")
        hoconParts.Add("akka.remote.dot-netty.tcp.port = 0")
        hoconParts.Add("akka.remote.dot-netty.tcp.hostname = 0.0.0.0")
        hoconParts.Add(sprintf "akka.remote.dot-netty.tcp.public-hostname = \"%s\"" hostname)
        hoconParts.Add("akka.remote.dot-netty.tcp.tcp-reuse-addr = on")
        hoconParts.Add("akka.remote.dot-netty.tcp.transport-protocol = tcp")
        hoconParts.Add("akka.remote.dot-netty.tcp.send-buffer-size = 30000000b")
        hoconParts.Add("akka.remote.dot-netty.tcp.receive-buffer-size = 30000000b")
        hoconParts.Add("akka.remote.dot-netty.tcp.message-frame-size = 30000000b")
        hoconParts.Add("akka.remote.dot-netty.tcp.maximum-frame-size = 30000000b")
        hoconParts.Add("akka.remote.dot-netty.tcp.connection-timeout = 10s")
        hoconParts.Add("akka.remote.dot-netty.tcp.tcp-keepalive = on")
        let hocon = String.Join("\n", hoconParts)

        let config = ConfigurationFactory.ParseString(hocon)
        actorSystem <- ActorSystem.Create(DEXActorGlobal.ClientAtorSystemName, config)

        let boundPort =
            try actorSystem.Settings.Config.GetString("akka.remote.dot-netty.tcp.port")
            with _ -> "?"
        logger.LogInformation(
            "ActorSystem '{Name}' 생성 완료 (bound-port: {Port}, host: {Host})",
            actorSystem.Name :> obj, boundPort :> obj, hostname :> obj)

    static member val ActorSystemSubject: Subject<obj> = new Subject<obj>() with get

    // 서버 actor refs
    static member val ServerShortTermActor: IActorRef = null with get, set
    static member val ServerLongTermActor: IActorRef = null with get, set
    static member val ServerStreamActor: IActorRef = null with get, set

    // 로컬 actor refs
    static member val SelfShortTermActor: IActorRef = null with get, set

    member _.ClientLogger = logger
    member _.ActorSystem = actorSystem
    member _.ClientName = clientName
    member _.ServerShortTermPath = serverShortTermPath
    member _.ServerLongTermPath = serverLongTermPath
    member _.ServerStreamPath = serverStreamPath

    /// 서버의 ShortTerm/LongTerm/Stream actor를 resolve
    member this.ConnectToServer() =
        let resolveTimeout = TimeSpan.FromSeconds(15.0)
        try
            logger.LogInformation("서버 actor resolve 시작: {Path} (timeout: {Timeout}s)",
                serverShortTermPath :> obj, resolveTimeout.TotalSeconds :> obj)
            DexaActorSystem.ServerShortTermActor <-
                actorSystem.ActorSelection(serverShortTermPath)
                    .ResolveOne(resolveTimeout).Result
            logger.LogInformation("서버 ShortTerm actor 연결 성공")

            DexaActorSystem.ServerLongTermActor <-
                actorSystem.ActorSelection(serverLongTermPath)
                    .ResolveOne(AkkaExt.DefaultAskTimeout).Result
            logger.LogInformation("서버 LongTerm actor 연결 성공")

            DexaActorSystem.ServerStreamActor <-
                actorSystem.ActorSelection(serverStreamPath)
                    .ResolveOne(AkkaExt.DefaultAskTimeout).Result
            logger.LogInformation("서버 Stream actor 연결 성공")
            true
        with ex ->
            DexaActorSystem.ServerShortTermActor <- null
            DexaActorSystem.ServerLongTermActor <- null
            DexaActorSystem.ServerStreamActor <- null
            let inner = if isNull ex.InnerException then ex else ex.InnerException
            logger.LogWarning("서버 actor 연결 실패: [{ExType}] {ExMsg}",
                inner.GetType().Name :> obj, inner.Message :> obj)
            false

    /// 서버 연결이 되어 있는지 확인
    member _.IsServerConnected =
        not (isNull DexaActorSystem.ServerShortTermActor)

    interface IDisposable with
        member _.Dispose() =
            if not (isNull actorSystem) then
                actorSystem.Terminate() |> ignore
                actorSystem.Dispose()
            disposables.Dispose()
