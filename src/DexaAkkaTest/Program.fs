open System
open System.Configuration
open System.IO
open System.Reactive.Linq
open System.Reflection
open System.Threading
open Akka.Actor
open DEX.Client
open DEX.Core.Actor
open DEX.Interfaces.Akka
open DEX.Interfaces.Global

// DexaBridge의 DummyListenerActor와 동일
type DummyListenerActor() =
    inherit UntypedActor()
    override _.OnReceive(_message: obj) = ()

let dexaPath = @"C:\Program Files (x86)\LS\DEXA\Client"

AppDomain.CurrentDomain.add_AssemblyResolve(ResolveEventHandler(fun _ args ->
    let name = AssemblyName(args.Name)
    let path = Path.Combine(dexaPath, name.Name + ".dll")
    if File.Exists(path) then Assembly.LoadFrom(path) else null))

let loadDexaConfig () =
    let configPath = Path.Combine(dexaPath, "DEXA.ClientApp.exe.config")
    if File.Exists(configPath) then
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
            printfn "DEXA config: %d개 설정 로드" nodes.Count

printfn "=== Akka 1.4 In-Process 테스트 ==="
printfn "런타임: %s" (Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
printfn "Akka: %s" (typeof<UntypedActor>.Assembly.GetName().Version.ToString())

loadDexaConfig ()
PathDefine.DataDirectory <- @"C:\ProgramData\LS\DEXA\Storage"

let serverIp = ConfigurationManager.AppSettings.["serverIp"]
let serverPort = int (ConfigurationManager.AppSettings.["serverPort"])
printfn "서버: %s:%d" serverIp serverPort

// 1. DEXClient 생성
printfn "\n--- 1. DEXClient 생성 ---"
let _dexaClient = DEXClient()
printfn "성공"

// 2. CommProxy 초기화 (DexaBridge Program.cs와 동일)
printfn "\n--- 2. CommProxy 초기화 ---"
// DEXActorSystem은 internal일 수 있으므로 reflection으로 접근
let dexaActorSystemType =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.pick (fun a ->
        try
            a.GetTypes()
            |> Array.tryFind (fun t -> t.Name = "DEXActorSystem")
            |> Option.map (fun t -> t)
        with _ -> None)
printfn "DEXActorSystem: %s" dexaActorSystemType.FullName
let proxy =
    let prop = dexaActorSystemType.GetProperty("CommProxy", BindingFlags.Public ||| BindingFlags.Static)
    prop.GetValue(null) :?> IDEXCommProxy
if isNull proxy then
    printfn "CommProxy: null"
    exit 1
printfn "CommProxy: %s" (proxy.GetType().Name)

// proxy.Name, proxy.ActorType 설정 (reflection)
proxy.Name <- "akka-test"
proxy.ActorType <- ActorType.Client

try
    proxy.Initialize(
        typeof<DummyListenerActor>,
        typeof<DEXClientShortTermActor>,
        typeof<DEXClientLongTermActor>,
        typeof<DEXClientStreamActor>)
    printfn "Initialize 성공!"
with ex ->
    printfn "Initialize 실패: [%s] %s" (ex.GetType().Name) ex.Message

// 3. 서버 연결
printfn "\n--- 3. 서버 연결 ---"
proxy.Connect(serverIp, serverPort)
printfn "Connect 호출 완료"

// 4. 연결 대기
printfn "\n--- 4. 서버 연결 대기 ---"
let mutable connected = false
for i in 1 .. 30 do
    if not connected then
        Thread.Sleep(1000)
        try
            let shortTerm =
                proxy.GetType().GetProperty("ShortTermActor").GetValue(proxy)
            let serverShortTerm =
                proxy.GetType().GetProperty("ServerShortTermActor").GetValue(proxy)
            if not (isNull shortTerm) && not (isNull serverShortTerm) then
                printfn "%d초: 서버 연결 성공!" i
                connected <- true
        with _ -> ()
        if not connected && i % 5 = 0 then
            printfn "%d초: 대기중..." i

if not connected then
    printfn "서버 연결 타임아웃"
    exit 1

// 5. AskServer 테스트 (전체 reflection)
printfn "\n--- 5. AskServer 테스트 ---"
try
    let proxyObj = proxy :> obj
    let askMethod =
        proxyObj.GetType().GetMethods()
        |> Array.find (fun m -> m.Name = "AskServer" && m.IsGenericMethod)

    // DEXA DLL에서 메시지 타입 찾기
    let findType name =
        AppDomain.CurrentDomain.GetAssemblies()
        |> Array.tryPick (fun a ->
            try a.GetTypes() |> Array.tryFind (fun t -> t.FullName = name) with _ -> None)
        |> Option.defaultWith (fun () -> failwithf "타입 %s 없음" name)

    let reqType = findType "DEX.Core.Actor.AmC2SRequestViewAssets"
    let replyType = findType "DEX.Core.Actor.AmS2CReplyViewAssets"
    printfn "요청 타입: %s (from %s)" reqType.FullName (reqType.Assembly.GetName().Name)
    printfn "응답 타입: %s (from %s)" replyType.FullName (replyType.Assembly.GetName().Name)

    let genericAsk = askMethod.MakeGenericMethod(replyType)
    let msg = Activator.CreateInstance(reqType, true)
    let taskObj = genericAsk.Invoke(proxyObj, [| msg |])
    let task = taskObj :?> System.Threading.Tasks.Task
    if task.Wait(TimeSpan.FromSeconds(30.0)) then
        let result = task.GetType().GetProperty("Result").GetValue(task)
        if isNull result then
            printfn "응답: null"
        else
            let viewAssets = result.GetType().GetProperty("ViewAssets").GetValue(result) :?> Array
            printfn "성공! ViewAssets %d건" viewAssets.Length
    else
        printfn "타임아웃 (30초)"
with ex ->
    let inner = if isNull ex.InnerException then ex else ex.InnerException
    printfn "Ask 실패: [%s] %s" (inner.GetType().Name) inner.Message

// 6. 알림 수신 테스트
printfn "\n--- 6. 알림 수신 대기 (10초) ---"
let notifCount = ref 0
DEXClientShortTermActor.DataChanged
    .Subscribe(fun (dc: DataChangedNotification) ->
        Interlocked.Increment(notifCount) |> ignore
        printfn "  알림: %s" (dc.GetType().Name))
|> ignore
Thread.Sleep(10000)
printfn "알림 수신: %d건" notifCount.Value

printfn "\n=== 테스트 완료 ==="
