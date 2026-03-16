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

type DexaActorSystem(logger: ILogger, options: DexaClientOptions) =

    static let hoconTemplate = """
akka {{
    size = 1000000000b
    timeout = 600s

    actor {{
        provider = remote
        serializers {{
            json = "DexaWeb.Dexa.Infrastructure.DexaJsonSerializer, DexaWeb.Dexa"
        }}
        serialization-bindings {{
            "System.Object" = json
        }}
        serialization-identifiers {{
            "DexaWeb.Dexa.Infrastructure.DexaJsonSerializer, DexaWeb.Dexa" = 1
        }}
    }}
    remote {{
        enabled-transports = ["akka.remote.dot-netty.tcp"]
        log-remote-lifecycle-events = on
        gate-invalid-addresses-for = 2s
        retry-gate-closed-for = 1s
        dot-netty.tcp {{
            tcp-reuse-addr = on
            transport-protocol = tcp
            port = 0
            hostname = "0.0.0.0"
            public-hostname = "{0}"
            send-buffer-size = ${{akka.size}}
            receive-buffer-size = ${{akka.size}}
            message-frame-size = ${{akka.size}}
            maximum-frame-size = ${{akka.size}}
            connection-timeout = ${{akka.timeout}}
            tcp-keepalive = on
        }}
        watch-failure-detector {{
            heartbeat-interval = ${{akka.timeout}}
            unreachable-nodes-reaper-interval = ${{akka.timeout}}
            expected-response-after = ${{akka.timeout}}
        }}
        transport-failure-detector {{
            heartbeat-interval = ${{akka.timeout}}
            acceptable-heartbeat-pause = ${{akka.timeout}}
        }}
    }}
}}"""

    let disposables = new CompositeDisposable()
    let mutable actorSystem: ActorSystem = null

    let serverActorPath =
        sprintf "akka.tcp://%s@%s:%d/user/%s"
            DEXActorGlobal.BackupServerActorSystemName
            options.ServerIp
            options.ServerPort
            DEXActorGlobal.GuardianActorName

    do
        DexaActorSystem.EnsureRemoteAssemblyLoaded()
        DexaAssemblyResolver.Register()
        AkkaExt.DefaultAskTimeout <- TimeSpan.FromSeconds(float options.AskTimeoutSeconds)
        AkkaExt.DefaultPingTimeout <- TimeSpan.FromSeconds(float options.PingTimeoutSeconds)

        let hostname = Environment.MachineName
        let hocon = String.Format(hoconTemplate, hostname)
        let config =
            ConfigurationFactory.ParseString(hocon)
                .WithFallback(ConfigurationFactory.Load())

        actorSystem <- ActorSystem.Create(DEXActorGlobal.ClientAtorSystemName, config)
        logger.LogInformation("ActorSystem 생성 완료. 서버 경로: {ServerActorPath}", serverActorPath :> obj)

    static member val ActorSystemSubject: Subject<obj> = new Subject<obj>() with get
    static member val TheServerActor: IActorRef = null with get, set
    static member val SelfGuardianActor: IActorRef = null with get, set

    member _.ClientLogger = logger
    member _.ActorSystem = actorSystem

    member _.ServerActorPath = serverActorPath

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member private EnsureRemoteAssemblyLoaded() =
        ignore typeof<RemoteSettings>

    member this.ConnectToServer() =
        try
            DexaActorSystem.TheServerActor <- actorSystem.GetServerActor(this.ServerActorPath, logger)
            logger.LogInformation("DEXA Server 연결 성공: {ServerActorPath}", this.ServerActorPath :> obj)
        with ex ->
            if ex.ToString().Contains("Akka.Actor.ActorNotFoundException") then
                logger.LogWarning("DEXA Server Actor를 찾을 수 없습니다. Guardian이 재연결을 시도합니다.")
            else
                logger.LogError(ex, "DEXA Server 연결 실패: {ServerActorPath}", this.ServerActorPath :> obj)

    interface IDisposable with
        member _.Dispose() =
            if not (isNull actorSystem) then
                actorSystem.Terminate() |> ignore
                actorSystem.Dispose()
            disposables.Dispose()
