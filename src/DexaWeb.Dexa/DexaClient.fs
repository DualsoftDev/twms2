namespace DexaWeb.Dexa

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Reactive.Subjects
open System.Text
open System.Threading
open System.Threading.Tasks
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open DEX.Core.Actor
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// Named Pipe 기반 DEXA Client.
/// DexaBridge(.NET 4.8)를 자식 프로세스로 실행하고 Named Pipe로 통신.
type DexaClient(options: IOptions<DexaClientOptions>, logger: ILogger<DexaClient>) =
    let opts = options.Value
    let mutable bridgeProcess: Process = null
    let mutable pipeClient: NamedPipeClientStream = null
    let mutable pipeReader: StreamReader = null
    let mutable pipeWriter: StreamWriter = null
    let mutable initialized = false
    let mutable disposed = false
    let mutable readTask: Task = null
    let notifications = new Subject<obj>()
    let writeLock = obj ()
    let pipeName = sprintf "DexaBridge_%d" (Process.GetCurrentProcess().Id)

    /// 대기 중인 Ask 요청
    let pendingRequests = ConcurrentDictionary<string, TaskCompletionSource<JToken>>()

    let jsonSettings = JsonSerializerSettings(
        NullValueHandling = NullValueHandling.Ignore,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Ignore)

    let deserializeSettings =
        let s = JsonSerializerSettings(
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Error = System.EventHandler<Serialization.ErrorEventArgs>(fun _ args ->
                args.ErrorContext.Handled <- true))
        s

    let sendRequest (method: string) (msgType: string) (payload: obj) (timeout: float option) =
        let id = Guid.NewGuid().ToString()
        let req = JObject()
        req.["id"] <- JToken.FromObject(id)
        req.["method"] <- JToken.FromObject(method)
        if not (isNull msgType) then
            req.["type"] <- JToken.FromObject(msgType)
        if not (isNull payload) then
            req.["payload"] <- JToken.FromObject(payload, JsonSerializer.Create(jsonSettings))
        match timeout with
        | Some t -> req.["timeout"] <- JToken.FromObject(t)
        | None -> ()
        id, req

    let writeLine (json: string) =
        lock writeLock (fun () ->
            if not (isNull pipeWriter) then
                pipeWriter.WriteLine(json)
                pipeWriter.Flush())

    let handleNotification (resp: JObject) =
        let notifType = resp.Value<string>("type")
        match notifType with
        | "Connected" ->
            let connected = resp.["payload"].Value<bool>("connected")
            logger.LogInformation("DexaBridge 연결 상태: {Connected}", connected :> obj)
            notifications.OnNext(DEX.Core.Actor.ConnectivityChangedNotification(connected))
        | "DatabaseChangedNotification" ->
            logger.LogDebug("DexaBridge DB 변경 알림 수신")
            try
                let p = resp.["payload"]
                let tableName =
                    if not (isNull p) then
                        p.Value<string>("TableName")
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> p.Value<string>("tableName") |> Option.ofObj |> Option.defaultValue "")
                    else ""
                let opStr =
                    if not (isNull p) then
                        p.Value<string>("DatabaseChangeOperation")
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> p.Value<string>("databaseChangeOperation") |> Option.ofObj |> Option.defaultValue "")
                    else ""
                let op =
                    match opStr.ToLowerInvariant() with
                    | "create" | "insert" -> DatabaseChangeOperation.Create
                    | "update" -> DatabaseChangeOperation.Update
                    | "delete" -> DatabaseChangeOperation.Delete
                    | _ -> DatabaseChangeOperation.Update
                notifications.OnNext(DatabaseChangedNotification(null, tableName, op))
            with ex ->
                logger.LogWarning("DexaBridge DatabaseChanged 파싱 실패: {Err}", ex.Message :> obj)
        | "RefreshNotification" ->
            logger.LogDebug("DexaBridge 전체 갱신 알림 수신")
            notifications.OnNext(DEX.Core.Actor.RefreshNotification())
        | "ServerLockStatusChangedNotification" ->
            notifications.OnNext(DEX.Core.Actor.ServerLockStatusChangedNotification(null))
        | "ReminderStatusChangeNotification" ->
            notifications.OnNext(DEX.Core.Actor.ReminderStatusChangeNotification(null))
        | _ ->
            logger.LogDebug("DexaBridge 알림: {Type}", notifType :> obj)

    let handleResponse (resp: JObject) =
        let id = resp.Value<string>("id")
        if isNull id then
            // Notification
            handleNotification resp
        else
            let mutable tcs = Unchecked.defaultof<_>
            if pendingRequests.TryRemove(id, &tcs) then
                let success = resp.Value<bool>("success")
                if success then
                    tcs.TrySetResult(resp.["payload"]) |> ignore
                else
                    let error = resp.Value<string>("error")
                    tcs.TrySetException(Exception(error)) |> ignore

    let mutable pipeConnected = false

    let readLoop () =
        task {
            try
                let mutable running = true
                while running && not disposed && not (isNull pipeReader) do
                    let! line = pipeReader.ReadLineAsync()
                    if isNull line then
                        logger.LogWarning("DexaBridge pipe 연결 끊김")
                        pipeConnected <- false
                        running <- false
                    else
                        try
                            let resp = JObject.Parse(line)
                            handleResponse resp
                        with ex ->
                            logger.LogWarning("DexaBridge 응답 파싱 실패: {Err}", ex.Message :> obj)
            with ex ->
                if not disposed then
                    logger.LogWarning("DexaBridge read loop 종료: {Err}", ex.Message :> obj)
                    pipeConnected <- false
        } :> Task

    let killOrphanBridgeProcesses () =
        try
            let currentPid = Process.GetCurrentProcess().Id
            for proc in Process.GetProcessesByName("DexaBridge") do
                try
                    // 현재 서버의 자식이 아닌 고아 프로세스 정리
                    logger.LogInformation("고아 DexaBridge 프로세스 종료: PID={Pid}", proc.Id :> obj)
                    proc.Kill()
                    proc.WaitForExit(3000) |> ignore
                    proc.Dispose()
                with ex ->
                    logger.LogWarning("DexaBridge 프로세스 종료 실패 (PID={Pid}): {Err}", proc.Id :> obj, ex.Message :> obj)
        with _ -> ()

    let startBridgeProcess () =
        // 이전 고아 프로세스 정리
        killOrphanBridgeProcesses ()

        // DexaBridge.exe 경로 찾기
        let basePath = AppContext.BaseDirectory
        let bridgePath = Path.Combine(basePath, "DexaBridge", "DexaBridge.exe")
        if not (File.Exists(bridgePath)) then
            // 개발 시 빌드 출력 경로
            let devPath = Path.Combine(basePath, "..", "..", "..", "..", "DexaBridge", "bin", "Debug", "net48", "DexaBridge.exe")
            let devPathResolved = Path.GetFullPath(devPath)
            if not (File.Exists(devPathResolved)) then
                failwithf "DexaBridge.exe not found at '%s' or '%s'" bridgePath devPathResolved
            devPathResolved
        else
            bridgePath

    interface IDexaClient with
        member _.IsConnected = initialized && pipeConnected && not (isNull pipeWriter)

        member _.PingServerAsync() =
            task {
                if not initialized || isNull pipeWriter then return false
                else
                    try
                        let id, req = sendRequest "ping" null null None
                        let tcs = TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously)
                        pendingRequests.[id] <- tcs
                        writeLine (req.ToString(Formatting.None))
                        let! _ = Task.WhenAny(tcs.Task, Task.Delay(3000))
                        if tcs.Task.IsCompleted then return true
                        else
                            pendingRequests.TryRemove(id) |> ignore
                            return false
                    with _ -> return false
            }

        member _.InitializeAsync() =
            task {
                try
                    let bridgeExe = startBridgeProcess ()
                    logger.LogInformation("DexaBridge 시작: {Path}", bridgeExe :> obj)

                    let psi = ProcessStartInfo(
                        FileName = bridgeExe,
                        Arguments = sprintf "--pipe %s" pipeName,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true)

                    bridgeProcess <- Process.Start(psi)
                    bridgeProcess.ErrorDataReceived.Add(fun e ->
                        if not (isNull e.Data) then
                            logger.LogInformation("[DexaBridge] {Line}", e.Data :> obj))
                    bridgeProcess.BeginErrorReadLine()

                    logger.LogInformation("DexaBridge PID={Pid}, pipe={Pipe}", bridgeProcess.Id :> obj, pipeName :> obj)

                    // 잠시 대기 후 pipe 연결
                    do! Task.Delay(2000)

                    pipeClient <- new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)
                    do! pipeClient.ConnectAsync(30000)
                    logger.LogInformation("DexaBridge Named Pipe 연결 성공")

                    logger.LogInformation("Pipe reader/writer 생성 중...")
                    let utf8NoBom = UTF8Encoding(false)
                    pipeReader <- new StreamReader(pipeClient, utf8NoBom)
                    let writer = new StreamWriter(pipeClient, utf8NoBom)
                    writer.AutoFlush <- true
                    pipeWriter <- writer
                    logger.LogInformation("Pipe reader/writer 생성 완료")

                    // 읽기 루프 시작
                    pipeConnected <- true
                    readTask <- readLoop ()
                    logger.LogInformation("Read loop 시작됨")

                    initialized <- true
                    logger.LogInformation("DexaClient 초기화 완료 (DexaBridge 방식)")
                with ex ->
                    initialized <- false
                    logger.LogError(ex, "DexaClient 초기화 실패")
            } :> Task

        member _.AskServerAsync<'T>(message: obj, ?timeout: TimeSpan) =
            if not initialized || isNull pipeWriter then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))

            let msgType = message.GetType().Name
            let actualTimeout = defaultArg timeout (TimeSpan.FromSeconds(float opts.AskTimeoutSeconds))

            let id, req = sendRequest "ask" msgType message (Some actualTimeout.TotalSeconds)
            let tcs = TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously)
            pendingRequests.[id] <- tcs

            writeLine (req.ToString(Formatting.None))

            // Timeout
            let cts = new CancellationTokenSource(actualTimeout)
            cts.Token.Register(fun () ->
                let mutable removed = Unchecked.defaultof<_>
                if pendingRequests.TryRemove(id, &removed) then
                    removed.TrySetException(
                        Akka.Actor.AskTimeoutException(sprintf "Timeout after %O" actualTimeout)) |> ignore
            ) |> ignore

            task {
                let! result = tcs.Task
                cts.Dispose()
                // JToken → 'T 변환 (역직렬화 에러 무시)
                if isNull result then return Unchecked.defaultof<'T>
                else
                    try
                        let serializer = JsonSerializer.Create(deserializeSettings)
                        return result.ToObject<'T>(serializer)
                    with _ ->
                        // 역직렬화 실패 시 기본값 (null) — 호출자가 null 체크
                        logger.LogWarning("DexaBridge 응답 역직렬화 실패 (type={Type}), 기본값 반환", typeof<'T>.Name :> obj)
                        return Unchecked.defaultof<'T>
            }

        member _.TellServer(message: obj) =
            if not initialized || isNull pipeWriter then
                raise (InvalidOperationException("DEXA Server에 연결되지 않았습니다."))
            let msgType = message.GetType().Name
            let _, req = sendRequest "tell" msgType message None
            writeLine (req.ToString(Formatting.None))

        member _.ServerNotifications = notifications :> IObservable<obj>

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                // 대기 중인 요청 모두 취소
                for kvp in pendingRequests do
                    kvp.Value.TrySetCanceled() |> ignore
                pendingRequests.Clear()
                notifications.OnCompleted()
                notifications.Dispose()
                try
                    if not (isNull pipeWriter) then pipeWriter.Dispose()
                    if not (isNull pipeReader) then pipeReader.Dispose()
                    if not (isNull pipeClient) then pipeClient.Dispose()
                with _ -> ()
                try
                    if not (isNull bridgeProcess) && not bridgeProcess.HasExited then
                        bridgeProcess.Kill()
                        bridgeProcess.WaitForExit(3000) |> ignore
                        bridgeProcess.Dispose()
                with _ -> ()
                initialized <- false
