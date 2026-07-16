namespace Twms2.Dexa

open System
open System.Configuration
open System.IO
open System.Reactive.Subjects
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open DEX.Core.Actor
open DEX.Interfaces.Akka
open DEX.Interfaces.Global
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// CommProxy.Initialize에 필요한 빈 actor stub (DexaBridge DummyListenerActor와 동일)
type DummyListenerActor() =
    inherit UntypedActor()
    override _.OnReceive(_message: obj) = ()

/// DEXA CommProxy In-Process 기반 Client.
/// DexaBridge 프로세스 없이 DEXA의 Akka 1.4 DLL을 .NET 10에서 직접 로드하여 사용.
type DexaDirectClient(options: IOptions<DexaClientOptions>, logger: ILogger<DexaDirectClient>) =
    let opts = options.Value
    let notifications = new Subject<obj>()
    let mutable proxy: IDEXCommProxy = Unchecked.defaultof<_>
    let mutable initialized = false
    let mutable disposed = false
    let mutable dataChangedSub: IDisposable = null
    let mutable messageSub: IDisposable = null
    let mutable reconnectCts: CancellationTokenSource = null
    let mutable lastConnectedState = false
    let dexaPath = opts.DllPath

    /// DEXA DLL 경로에서 어셈블리 resolve
    static let mutable resolverRegistered = false
    static let registerResolver (path: string) =
        if not resolverRegistered then
            resolverRegistered <- true
            AppDomain.CurrentDomain.add_AssemblyResolve(ResolveEventHandler(fun _ args ->
                let name = AssemblyName(args.Name)
                let dllPath = Path.Combine(path, name.Name + ".dll")
                if File.Exists(dllPath) then Assembly.LoadFrom(dllPath) else null))

    /// DexaBridge와 동일: DEXA ClientApp.exe.config에서 appSettings 로드
    let loadDexaConfig () =
        let configPath = Path.Combine(dexaPath, "DEXA.ClientApp.exe.config")
        if File.Exists(configPath) then
            try
                let doc = Xml.XmlDocument()
                doc.Load(configPath)
                let nodes = doc.SelectNodes("//appSettings/add")
                if not (isNull nodes) then
                    let cfg = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None)
                    for i in 0 .. nodes.Count - 1 do
                        let node = nodes.[i]
                        let key = node.Attributes.["key"]
                        let value = node.Attributes.["value"]
                        if not (isNull key) && not (isNull value) then
                            if not (isNull (cfg.AppSettings.Settings.[key.Value])) then
                                cfg.AppSettings.Settings.[key.Value].Value <- value.Value
                            else
                                cfg.AppSettings.Settings.Add(key.Value, value.Value)
                    cfg.Save(ConfigurationSaveMode.Modified)
                    ConfigurationManager.RefreshSection("appSettings")
                    logger.LogInformation("DEXA config 로드 완료 ({Count}개 설정)", nodes.Count :> obj)
            with ex ->
                logger.LogWarning("DEXA config 로드 실패: {Err}", ex.Message :> obj)
        else
            logger.LogWarning("DEXA config 파일 없음: {Path}", configPath :> obj)

    /// CommProxy의 ShortTermActor/ServerShortTermActor 접근 (reflection)
    let getProxyProp (name: string) =
        try proxy.GetType().GetProperty(name).GetValue(proxy :> obj)
        with _ -> null

    /// 현재 연결 여부 확인
    let isConnectedNow () =
        initialized && not disposed
        && not (isNull (getProxyProp "ShortTermActor"))
        && not (isNull (getProxyProp "ServerShortTermActor"))

    /// 백그라운드 재연결 루프: 연결이 끊겨있으면 주기적으로 proxy.Connect() 재시도
    let startReconnectLoop () =
        reconnectCts <- new CancellationTokenSource()
        let ct = reconnectCts.Token
        Task.Run(Func<Task>(fun () ->
            task {
                while not ct.IsCancellationRequested && not disposed do
                    try do! Task.Delay(10_000, ct) with :? OperationCanceledException -> ()
                    if ct.IsCancellationRequested || disposed then ()
                    else
                        let connected = isConnectedNow ()
                        // 상태 변경 감지 시 알림
                        if connected <> lastConnectedState then
                            lastConnectedState <- connected
                            logger.LogInformation("DEXA 연결 상태 변경 감지: {Connected}", connected :> obj)
                            notifications.OnNext(ConnectivityChangedNotification(connected))
                        // 연결 안 되어있으면 재연결 시도
                        if not connected && initialized && not disposed then
                            try
                                logger.LogInformation("DEXA 서버 재연결 시도: {Ip}:{Port}", opts.ServerIp :> obj, opts.ServerPort :> obj)
                                proxy.Connect(opts.ServerIp, opts.ServerPort)
                                // 연결 대기 (최대 10초)
                                let mutable reconnected = false
                                for i in 1 .. 10 do
                                    if not reconnected && not ct.IsCancellationRequested then
                                        try do! Task.Delay(1000, ct) with :? OperationCanceledException -> ()
                                        let shortTerm = getProxyProp "ShortTermActor"
                                        let serverShortTerm = getProxyProp "ServerShortTermActor"
                                        if not (isNull shortTerm) && not (isNull serverShortTerm) then
                                            reconnected <- true
                                if reconnected then
                                    lastConnectedState <- true
                                    logger.LogInformation("DEXA 서버 재연결 성공")
                                    notifications.OnNext(ConnectivityChangedNotification(true))
                            with ex ->
                                logger.LogWarning("DEXA 서버 재연결 실패: {Err}", ex.Message :> obj)
            }), ct) |> ignore

    /// CommProxy.AskServer<T>(message) 호출 (reflection - generic method)
    let mutable askMethodCache: MethodInfo = null
    let getAskMethod () =
        if isNull askMethodCache then
            askMethodCache <-
                (proxy :> obj).GetType().GetMethods()
                |> Array.tryFind (fun m -> m.Name = "AskServer" && m.IsGenericMethod)
                |> Option.defaultValue null
        askMethodCache

    /// F# 재정의 타입(Twms2.Dexa 어셈블리) → DEXA DLL 타입(DEXA.Core.Actor 어셈블리) 변환.
    /// CommProxy의 Akka serializer가 $type에 올바른 어셈블리명을 사용하도록.
    /// DexaBridge의 MessageFactory.Create와 동일한 역할.
    let dexaCoreActorAsm = lazy (
        AppDomain.CurrentDomain.GetAssemblies()
        |> Array.tryFind (fun a -> a.GetName().Name = "DEXA.Core.Actor")
        |> Option.defaultWith (fun () ->
            Assembly.LoadFrom(Path.Combine(dexaPath, "DEXA.Core.Actor.dll"))))

    let jsonSettings = Newtonsoft.Json.JsonSerializerSettings(
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
        MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore,
        ConstructorHandling = Newtonsoft.Json.ConstructorHandling.AllowNonPublicDefaultConstructor,
        ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)

    /// DexaBridge MessageFactory.TypeAliases와 동일: F# 래퍼 타입명 → DEXA 원본 타입명
    let typeAliases =
        dict [
            "AmC2SExecuteTriggerOnce", "AmC2SExecuteTestEvent"
        ]

    /// DexaBridge MessageFactory.Create와 동일한 방식으로 DEXA DLL 타입 인스턴스 생성.
    /// reflection으로 생성자 파라미터 매칭 + 프로퍼티 설정 + EnsureSender.
    let toDexaType (message: obj) =
        let msgType = message.GetType()
        // 이미 DEXA DLL 타입이면 그대로 반환
        if msgType.Assembly.GetName().Name = "DEXA.Core.Actor" then
            message
        else
            // 타입 별칭 적용
            let mutable targetName = ""
            let typeName =
                if typeAliases.TryGetValue(msgType.Name, &targetName) then
                    msgType.Namespace + "." + targetName
                else
                    msgType.FullName
            // DEXA DLL에서 타입 찾기
            let dexaType = dexaCoreActorAsm.Value.GetType(typeName)
            if isNull dexaType then
                logger.LogWarning("DEXA DLL에서 타입 {Type}을 찾을 수 없음, 원본 사용", typeName :> obj)
                message
            else
                // 원본 객체의 프로퍼티를 JObject로 추출
                let payload =
                    let json = Newtonsoft.Json.JsonConvert.SerializeObject(message, jsonSettings)
                    Newtonsoft.Json.Linq.JObject.Parse(json)

                // DexaBridge MessageFactory.Create 방식: 생성자 파라미터 매칭
                let ctors =
                    dexaType.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance)
                    |> Array.sortBy (fun c -> c.GetParameters().Length)

                let mutable result: obj = null
                for ctor in ctors do
                    if isNull result then
                        try
                            let parameters = ctor.GetParameters()
                            let args = Array.zeroCreate<obj> parameters.Length
                            for i in 0 .. parameters.Length - 1 do
                                let param = parameters.[i]
                                let paramType = param.ParameterType
                                // CommProxy 타입이면 proxy 전달
                                if paramType.Name.Contains("DEXCommProxy") || paramType.Name.Contains("IDEXCommProxy")
                                   || (not (isNull (proxy :> obj)) && paramType.IsInstanceOfType(proxy)) then
                                    args.[i] <- proxy :> obj
                                else
                                    // payload에서 값 찾기 (대소문자 무시)
                                    let token =
                                        payload.Properties()
                                        |> Seq.tryFind (fun p -> String.Equals(p.Name, param.Name, StringComparison.OrdinalIgnoreCase))
                                        |> Option.map (fun p -> p.Value)
                                    match token with
                                    | Some t ->
                                        args.[i] <- t.ToObject(paramType)
                                    | None ->
                                        if param.HasDefaultValue then
                                            args.[i] <- param.DefaultValue
                                        elif not paramType.IsValueType || Nullable.GetUnderlyingType(paramType) <> null then
                                            args.[i] <- null
                                        else
                                            args.[i] <- Activator.CreateInstance(paramType)
                            result <- ctor.Invoke(args)
                        with _ -> ()

                // 생성자로 안 되면 JSON 역직렬화 fallback
                if isNull result then
                    result <- Newtonsoft.Json.JsonConvert.DeserializeObject(payload.ToString(), dexaType, jsonSettings)

                // 프로퍼티 추가 설정 (ApplyPayloadProperties)
                if not (isNull result) then
                    for prop in payload.Properties() do
                        try
                            let pi = dexaType.GetProperty(prop.Name, BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.IgnoreCase)
                            if not (isNull pi) && pi.CanWrite then
                                let v = prop.Value.ToObject(pi.PropertyType)
                                pi.SetValue(result, v)
                        with _ -> ()

                    // EnsureSender
                    try
                        let senderProp = dexaType.GetProperty("Sender", BindingFlags.Public ||| BindingFlags.Instance)
                        if not (isNull senderProp) && senderProp.CanWrite
                           && isNull (senderProp.GetValue(result))
                           && senderProp.PropertyType.IsInstanceOfType(proxy) then
                            senderProp.SetValue(result, proxy)
                    with _ -> ()

                logger.LogInformation("toDexaType: {From} → {To} (asm={Asm})",
                    msgType.Name :> obj, dexaType.Name :> obj, dexaType.Assembly.GetName().Name :> obj)
                if isNull result then message else result

    /// Ask 응답 타입 해석: F# 래퍼 타입(Twms2.Dexa)이면 동명의 DEXA DLL 타입으로.
    /// 서버 응답은 DEXA.Core.Actor 어셈블리 타입으로 역직렬화되므로,
    /// 래퍼 타입 그대로 AskServer<T>를 부르면 타입 정체성이 달라 영원히 매칭되지 않는다(타임아웃).
    let resolveDexaReplyType (t: Type) =
        if t.Assembly.GetName().Name = "DEXA.Core.Actor" then t
        else
            let dt = dexaCoreActorAsm.Value.GetType(t.FullName)
            if isNull dt then t else dt

    /// DEXA DLL 응답 → 호출자 래퍼 타입 인스턴스로 변환.
    /// 동명·타입호환 프로퍼티만 얕은 복사 — ActorRef 등 DEXA 공유 타입은 참조 그대로 보존.
    let convertReplyTo (targetType: Type) (reply: obj) : obj =
        if isNull reply then null
        elif targetType.IsInstanceOfType(reply) then reply
        else
            let wrapper = Activator.CreateInstance(targetType, true)
            for p in targetType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance) do
                if p.CanWrite then
                    let sp = reply.GetType().GetProperty(p.Name)
                    if not (isNull sp) && sp.CanRead && p.PropertyType.IsAssignableFrom(sp.PropertyType) then
                        try p.SetValue(wrapper, sp.GetValue(reply)) with _ -> ()
            wrapper

    /// DEXA DLL 알림 → F# 재정의 알림 타입으로 변환
    /// DexaNotificationService가 F# 타입으로 switch/pattern match하므로 필수
    let convertNotification (notification: obj) : obj =
        let typeName = notification.GetType().Name
        match typeName with
        | "DatabaseChangedNotification" ->
            try
                let tableName =
                    let p = notification.GetType().GetProperty("TableName")
                    if isNull p then "" else p.GetValue(notification) :?> string |> Option.ofObj |> Option.defaultValue ""
                let opVal =
                    let p = notification.GetType().GetProperty("DatabaseChangeOperation")
                    if isNull p then 1 else p.GetValue(notification) :?> int
                let op: DatabaseChangeOperation = LanguagePrimitives.EnumOfValue opVal
                DatabaseChangedNotification(null, tableName, op) :> obj
            with _ -> notification
        | "RefreshNotification" ->
            RefreshNotification() :> obj
        | "ServerLockStatusChangedNotification" ->
            ServerLockStatusChangedNotification(null) :> obj
        | "ReminderStatusChangeNotification" ->
            ReminderStatusChangeNotification(null) :> obj
        | "ConnectivityChangedNotification" ->
            try
                let connected =
                    let p = notification.GetType().GetProperty("Connected")
                    if isNull p then false else p.GetValue(notification) :?> bool
                ConnectivityChangedNotification(connected) :> obj
            with _ -> notification
        | _ -> notification

    interface IDexaClient with
        member _.IsConnected =
            initialized && not disposed
            && not (isNull (getProxyProp "ShortTermActor"))
            && not (isNull (getProxyProp "ServerShortTermActor"))

        member _.PingServerAsync() =
            task {
                if not initialized then return false
                else
                    try
                        let shortTerm = getProxyProp "ShortTermActor"
                        let serverShortTerm = getProxyProp "ServerShortTermActor"
                        return not (isNull shortTerm) && not (isNull serverShortTerm)
                    with _ ->
                        return false
            }

        member _.InitializeAsync() =
            task {
                try
                    logger.LogInformation("DexaDirectClient 초기화 시작 (CommProxy in-process)")

                    // 1. AssemblyResolve 등록
                    registerResolver dexaPath

                    // 2. DEXA config 로드
                    loadDexaConfig ()

                    // 3. PathDefine.DataDirectory 설정
                    PathDefine.DataDirectory <- @"C:\ProgramData\LS\DEXA\Storage"

                    // 4. DEXClient 생성 → CommProxy 자동 생성
                    let _dexaClient = DEX.Client.DEXClient()
                    logger.LogInformation("DEXClient 생성 성공")

                    // 5. CommProxy 접근
                    let dexaActorSystemType =
                        AppDomain.CurrentDomain.GetAssemblies()
                        |> Array.pick (fun a ->
                            try
                                a.GetTypes()
                                |> Array.tryFind (fun t -> t.Name = "DEXActorSystem")
                            with _ -> None)
                    let commProxyProp = dexaActorSystemType.GetProperty("CommProxy", BindingFlags.Public ||| BindingFlags.Static)
                    proxy <- commProxyProp.GetValue(null) :?> IDEXCommProxy
                    logger.LogInformation("CommProxy: {Type}", proxy.GetType().Name :> obj)

                    // 6. CommProxy 설정
                    proxy.Name <- opts.ClientName
                    proxy.ActorType <- ActorType.Client

                    // 7. CommProxy.Initialize
                    try
                        proxy.Initialize(
                            typeof<DummyListenerActor>,
                            typeof<DEX.Client.DEXClientShortTermActor>,
                            typeof<DEX.Client.DEXClientLongTermActor>,
                            typeof<DEX.Client.DEXClientStreamActor>)
                        logger.LogInformation("CommProxy Initialize 성공")
                    with ex ->
                        logger.LogError(ex, "CommProxy Initialize 실패")

                    // 8. 서버 연결
                    proxy.Connect(opts.ServerIp, opts.ServerPort)
                    logger.LogInformation("서버 연결 시작: {Ip}:{Port}", opts.ServerIp :> obj, opts.ServerPort :> obj)

                    // 9. 연결 대기 (최대 30초)
                    let mutable connected = false
                    for i in 1 .. 30 do
                        if not connected then
                            do! Task.Delay(1000)
                            let shortTerm = getProxyProp "ShortTermActor"
                            let serverShortTerm = getProxyProp "ServerShortTermActor"
                            if not (isNull shortTerm) && not (isNull serverShortTerm) then
                                logger.LogInformation("서버 연결 성공 ({Sec}초)", i :> obj)
                                connected <- true

                    if not connected then
                        logger.LogWarning("서버 연결 타임아웃 (30초)")

                    // 10. 알림 구독
                    // DEXA DLL 알림 타입 → F# 재정의 타입으로 변환하여 전달
                    // (DexaNotificationService가 F# 타입으로 패턴 매치하므로)
                    dataChangedSub <-
                        DEX.Client.DEXClientShortTermActor.DataChanged
                            .Subscribe(fun dc ->
                                let converted = convertNotification dc
                                notifications.OnNext(converted))
                    messageSub <-
                        DEX.Client.DEXClientShortTermActor.MessageSubject
                            .Subscribe(fun msg ->
                                let converted = convertNotification msg
                                notifications.OnNext(converted))

                    if connected then
                        notifications.OnNext(ConnectivityChangedNotification(true))

                    initialized <- true
                    lastConnectedState <- connected
                    logger.LogInformation("DexaDirectClient 초기화 완료 (CommProxy in-process)")

                    // 백그라운드 재연결 루프 시작
                    startReconnectLoop ()
                with ex ->
                    initialized <- false
                    logger.LogError(ex, "DexaDirectClient 초기화 실패")
            } :> Task

        member _.AskServerAsync<'T>(message: obj, ?timeout: TimeSpan) =
            if not initialized then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))

            task {
                try
                    let askMethod = getAskMethod ()
                    if isNull askMethod then
                        return raise (InvalidOperationException("CommProxy.AskServer 메서드를 찾을 수 없습니다."))
                    else
                        // 응답은 DEXA DLL 타입으로 도착하므로 래퍼 타입이 아닌 동명 DEXA 타입으로 Ask해야 매칭된다
                        let replyType = resolveDexaReplyType typeof<'T>
                        let genericAsk = askMethod.MakeGenericMethod(replyType)
                        let dexaMsg = toDexaType message
                        let taskObj = genericAsk.Invoke(proxy :> obj, [| dexaMsg |])
                        let innerTask = taskObj :?> Task
                        let actualTimeout = defaultArg timeout (TimeSpan.FromSeconds(float opts.AskTimeoutSeconds))
                        let! completed = Task.WhenAny(innerTask, Task.Delay(actualTimeout))
                        if obj.ReferenceEquals(completed, innerTask) then
                            let dexaReply = taskObj.GetType().GetProperty("Result").GetValue(taskObj)
                            match convertReplyTo typeof<'T> dexaReply with
                            | null -> return Unchecked.defaultof<'T>
                            | converted -> return converted :?> 'T
                        else
                            return raise (TimeoutException(sprintf "Ask 타임아웃: %s (%O)" (message.GetType().Name) actualTimeout))
                with
                | :? TargetInvocationException as ex when not (isNull ex.InnerException) ->
                    logger.LogWarning("Ask 실패: {Type} - {Err}",
                        message.GetType().Name :> obj, ex.InnerException.Message :> obj)
                    return raise ex.InnerException
                | ex ->
                    logger.LogWarning("Ask 실패: {Type} - {Err}",
                        message.GetType().Name :> obj, ex.Message :> obj)
                    return raise ex
            }

        member _.TellServer(message: obj) =
            if not initialized then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))
            let converted = toDexaType message
            logger.LogInformation("TellServer: {Type}", converted.GetType().FullName :> obj)
            proxy.TellServer(converted)

        member _.ServerNotifications = notifications :> IObservable<obj>

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                if not (isNull reconnectCts) then
                    reconnectCts.Cancel()
                    reconnectCts.Dispose()
                if not (isNull dataChangedSub) then dataChangedSub.Dispose()
                if not (isNull messageSub) then messageSub.Dispose()
                notifications.OnCompleted()
                notifications.Dispose()
                initialized <- false
