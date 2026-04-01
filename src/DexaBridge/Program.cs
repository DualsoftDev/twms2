using DEX.Client;
using DEX.Common;
using DEX.Core.Actor;
using DEX.Interfaces.Akka;
using DEX.Interfaces.Global;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Configuration;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DexaBridge
{
    class Program
    {
        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };

        static string _pipeName;
        static DEXClient _dexaClient;
        static volatile bool _running = true;
        static StreamWriter _pipeWriter;
        static readonly object _writeLock = new object();

        // CommProxy.Ask<T> 메서드 캐시
        static MethodInfo _askMethodGeneric;

        static void Main(string[] args)
        {
            try
            {
                ParseArgs(args);

                // DEXA ClientApp의 config를 사용하여 설정값 로드
                LoadDexaClientConfig();

                Console.Error.WriteLine($"[DexaBridge] Starting... pipe={_pipeName}");
                Console.Error.WriteLine($"[DexaBridge] serverIp={ConfigurationManager.AppSettings["serverIp"]}, serverPort={ConfigurationManager.AppSettings["serverPort"]}");

                PathDefine.DataDirectory = @"C:\ProgramData\LS\DEXA\Storage";

                _dexaClient = new DEXClient();

                // CommProxy 초기화
                InitializeCommProxy();

                FindAskMethod();
                SubscribeNotifications();

                // pipe 서버를 백그라운드에서 시작 (Connect/핸드셰이크와 병렬)
                var pipeThread = new Thread(() => RunPipeServer()) { IsBackground = true };
                pipeThread.Start();

                // 서버 핸드셰이크 완료 대기 (pipe는 이미 listening)
                WaitForConnection();

                Console.Error.WriteLine("[DexaBridge] DEXA connected, pipe server already running");

                // 메인 스레드는 프로세스 종료 방지
                pipeThread.Join();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DexaBridge] Fatal: {ex}");
                Environment.Exit(1);
            }
        }

        static void InitializeCommProxy()
        {
            var proxy = DEXActorSystem.CommProxy;
            if (proxy == null)
            {
                Console.Error.WriteLine("[DexaBridge] ERROR: CommProxy is null after DEXClient creation");
                return;
            }

            var serverIp = ConfigurationManager.AppSettings["serverIp"];
            var serverPort = int.Parse(ConfigurationManager.AppSettings["serverPort"]);

            proxy.Name = "twm-web";
            proxy.ActorType = DEX.Interfaces.Akka.ActorType.Client;

            Console.Error.WriteLine("[DexaBridge] CommProxy.Initialize 호출...");

            // IDEXCommProxy 인터페이스로 캐스트하여 직접 호출
            var commProxy = (IDEXCommProxy)proxy;
            try
            {
                commProxy.Initialize(
                    typeof(DummyListenerActor),
                    typeof(DEXClientShortTermActor),
                    typeof(DEX.Client.DEXClientLongTermActor),
                    typeof(DEX.Client.DEXClientStreamActor));
                Console.Error.WriteLine("[DexaBridge] Initialize 성공");
            }
            catch (Exception ex)
            {
                // 전체 예외 체인 출력
                var e = ex;
                int depth = 0;
                while (e != null)
                {
                    Console.Error.WriteLine($"[DexaBridge] Initialize error [{depth}]: {e.GetType().Name}: {e.Message}");
                    if (depth == 0) Console.Error.WriteLine($"[DexaBridge] Stack: {e.StackTrace}");
                    e = e.InnerException;
                    depth++;
                }
            }

            Console.Error.WriteLine($"[DexaBridge] CommProxy.Connect({serverIp}:{serverPort}) 호출...");
            proxy.Connect(serverIp, serverPort);
            Console.Error.WriteLine("[DexaBridge] CommProxy 초기화 완료");
        }

        static void FindAskMethod()
        {
            // IDEXCommProxy 인터페이스에서 Ask<T> 메서드 찾기
            if (DEXActorSystem.CommProxy == null)
            {
                Console.Error.WriteLine("[DexaBridge] Warning: CommProxy is null, will retry later");
                return;
            }

            var proxyType = DEXActorSystem.CommProxy.GetType();
            Console.Error.WriteLine($"[DexaBridge] CommProxy type: {proxyType.FullName}");

            // Ask 메서드 탐색 — 모든 인터페이스 포함
            var methods = proxyType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Ask" && m.IsGenericMethod)
                .ToArray();

            Console.Error.WriteLine($"[DexaBridge] Found {methods.Length} Ask methods");
            foreach (var m in methods)
            {
                var parms = m.GetParameters();
                Console.Error.WriteLine($"[DexaBridge]   Ask<T>({string.Join(", ", parms.Select(p => p.ParameterType.Name + " " + p.Name))})");
            }

            // 가장 적합한 Ask 메서드 선택 (object message 파라미터를 가진 것)
            _askMethodGeneric = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 1 && p[0].ParameterType == typeof(object);
            }) ?? methods.FirstOrDefault();

            if (_askMethodGeneric != null)
                Console.Error.WriteLine($"[DexaBridge] Selected Ask method: {_askMethodGeneric}");
            else
                Console.Error.WriteLine("[DexaBridge] Warning: No Ask method found on CommProxy!");
        }

        /// <summary>
        /// DEXA ClientApp의 config에서 appSettings 값을 로드.
        /// DexaBridge 자체 App.config보다 DEXA 설치 경로의 설정이 우선.
        /// </summary>
        static void LoadDexaClientConfig()
        {
            var dexaConfigPath = ConfigurationManager.AppSettings["DexaConfigPath"]
                ?? @"C:\Program Files (x86)\LS\DEXA\Client\DEXA.ClientApp.exe.config";
            if (!System.IO.File.Exists(dexaConfigPath))
            {
                Console.Error.WriteLine($"[DexaBridge] DEXA config not found: {dexaConfigPath}");
                return;
            }

            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(dexaConfigPath);
                var nodes = doc.SelectNodes("//appSettings/add");
                if (nodes == null) return;

                var cfg = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                foreach (System.Xml.XmlNode node in nodes)
                {
                    var key = node.Attributes?["key"]?.Value;
                    var value = node.Attributes?["value"]?.Value;
                    if (key != null && value != null)
                    {
                        if (cfg.AppSettings.Settings[key] != null)
                            cfg.AppSettings.Settings[key].Value = value;
                        else
                            cfg.AppSettings.Settings.Add(key, value);
                    }
                }
                cfg.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection("appSettings");
                Console.Error.WriteLine($"[DexaBridge] DEXA config 로드 완료 ({nodes.Count}개 설정)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DexaBridge] DEXA config 로드 실패: {ex.Message}");
            }
        }

        static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--pipe":
                        _pipeName = args[++i];
                        break;
                    // --server-ip, --server-port는 무시 (DEXA config에서 로드)
                    case "--server-ip":
                    case "--server-port":
                        i++; // 값 건너뜀
                        break;
                }
            }

            if (string.IsNullOrEmpty(_pipeName))
                _pipeName = "DexaBridge_" + System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        static object GetProxyProp(object proxy, string name)
        {
            return proxy?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(proxy);
        }

        static void WaitForConnection()
        {
            Console.Error.WriteLine("[DexaBridge] Waiting for DEXA server connection (actors)...");
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                try
                {
                    var cp = DEXActorSystem.CommProxy;
                    if (cp == null) continue;

                    var shortTerm = GetProxyProp(cp, "ShortTermActor");
                    var serverShortTerm = GetProxyProp(cp, "ServerShortTermActor");

                    if (shortTerm != null && serverShortTerm != null)
                    {
                        Console.Error.WriteLine($"[DexaBridge] ShortTermActor ready: {shortTerm}");
                        Console.Error.WriteLine($"[DexaBridge] ServerShortTermActor ready: {serverShortTerm}");
                        if (_askMethodGeneric == null)
                            FindAskMethod();
                        return;
                    }
                    if (i % 10 == 9)
                    {
                        Console.Error.WriteLine($"[DexaBridge] Still waiting... ShortTerm={shortTerm}, ServerShortTerm={serverShortTerm}");
                    }
                }
                catch { }
            }

            Console.Error.WriteLine("[DexaBridge] Warning: Server actors not ready after 30s, continuing anyway");
            if (_askMethodGeneric == null)
                FindAskMethod();
        }

        static void SubscribeNotifications()
        {
            DEXClientShortTermActor.DataChanged
                .Subscribe(dc =>
                {
                    try { SendNotification(dc.GetType().Name, dc); }
                    catch (Exception ex) { Console.Error.WriteLine($"[DexaBridge] Notification error: {ex.Message}"); }
                });

            DEXClientShortTermActor.MessageSubject
                .Where(msg => !(msg is DataChangedNotification))
                .Subscribe(msg =>
                {
                    try { SendNotification(msg.GetType().Name, msg); }
                    catch (Exception ex) { Console.Error.WriteLine($"[DexaBridge] Message error: {ex.Message}"); }
                });
        }

        static void SendNotification(string type, object payload)
        {
            try
            {
                WritePipeMessage(PipeResponse.Notify(type, payload));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DexaBridge] SendNotification error ({type}): {ex.Message}");
            }
        }

        static void WritePipeMessage(PipeResponse response)
        {
            lock (_writeLock)
            {
                if (_pipeWriter == null) return;
                try
                {
                    // 직렬화 먼저 (pipe에 쓰기 전에 에러 확인)
                    var json = JsonConvert.SerializeObject(response, Formatting.None, JsonSettings);
                    if (json == null) return;
                    _pipeWriter.WriteLine(json);
                    _pipeWriter.Flush();
                }
                catch (JsonSerializationException jex)
                {
                    // 직렬화 실패 — pipe는 유지, 이 메시지만 스킵
                    Console.Error.WriteLine($"[DexaBridge] Serialize error (skipped): {jex.Message}");
                }
                catch (System.IO.IOException)
                {
                    // pipe 끊김
                    Console.Error.WriteLine("[DexaBridge] Pipe broken, clearing writer");
                    _pipeWriter = null;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DexaBridge] Write error: {ex.Message}");
                }
            }
        }

        static void RunPipeServer()
        {
            while (_running)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(
                        _pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        Console.Error.WriteLine($"[DexaBridge] Waiting for pipe connection on '{_pipeName}'...");
                        pipe.WaitForConnection();
                        Console.Error.WriteLine("[DexaBridge] Pipe client connected");

                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false)))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)))
                        {
                            writer.AutoFlush = true;
                            lock (_writeLock)
                                _pipeWriter = writer;

                            WritePipeMessage(PipeResponse.Notify("Connected",
                                new { connected = DEXActorSystem.CommProxy != null }));

                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                var captured = line;
                                Task.Run(() => HandleRequest(captured));
                            }
                        }

                        lock (_writeLock)
                            _pipeWriter = null;

                        Console.Error.WriteLine("[DexaBridge] Pipe client disconnected");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DexaBridge] Pipe error: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
        }

        static void HandleRequest(string line)
        {
            PipeRequest request = null;
            try
            {
                request = JsonConvert.DeserializeObject<PipeRequest>(line);
                if (request == null) return;

                Console.Error.WriteLine($"[DexaBridge] Request: {request.Method} {request.Type}");

                PipeResponse response;
                switch (request.Method)
                {
                    case "ping":
                        response = PipeResponse.Ok(request.Id, "pong", new { alive = true });
                        break;
                    case "status":
                        response = PipeResponse.Ok(request.Id, "status", new
                        {
                            connected = DEXActorSystem.CommProxy != null,
                            pipeName = _pipeName
                        });
                        break;
                    case "ask":
                        response = HandleAsk(request);
                        break;
                    case "tell":
                        HandleTell(request);
                        response = PipeResponse.Ok(request.Id, "told", null);
                        break;
                    default:
                        response = PipeResponse.Fail(request.Id, $"Unknown method: {request.Method}");
                        break;
                }

                WritePipeMessage(response);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DexaBridge] HandleRequest error: {ex}");
                WritePipeMessage(PipeResponse.Fail(request?.Id, ex.Message));
            }
        }

        /// <summary>
        /// 리플렉션으로 CommProxy.Ask&lt;T&gt;(message)를 정확한 T 타입으로 호출
        /// </summary>
        static MethodInfo _askServerMethodGeneric;

        static object CallAskServerGeneric(object proxy, Type replyType, object message)
        {
            if (_askServerMethodGeneric == null)
            {
                _askServerMethodGeneric = proxy.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "AskServer" && m.IsGenericMethod)
                    .FirstOrDefault();

                // 인터페이스에서도 탐색
                if (_askServerMethodGeneric == null)
                {
                    foreach (var iface in proxy.GetType().GetInterfaces())
                    {
                        _askServerMethodGeneric = iface.GetMethods()
                            .FirstOrDefault(m => m.Name == "AskServer" && m.IsGenericMethod);
                        if (_askServerMethodGeneric != null) break;
                    }
                }

                Console.Error.WriteLine($"[DexaBridge] AskServer method: {_askServerMethodGeneric}");
            }

            if (_askServerMethodGeneric == null)
            {
                Console.Error.WriteLine("[DexaBridge] AskServer not found, falling back to Ask");
                return CallAskGeneric(proxy, replyType, message);
            }

            var genericMethod = _askServerMethodGeneric.MakeGenericMethod(replyType);
            return genericMethod.Invoke(proxy, new object[] { message });
        }

        static object CallAskGeneric(object proxy, Type replyType, object message)
        {
            if (_askMethodGeneric == null)
                FindAskMethod();
            if (_askMethodGeneric == null)
                throw new InvalidOperationException("CommProxy.Ask method not found");

            var genericMethod = _askMethodGeneric.MakeGenericMethod(replyType);
            Console.Error.WriteLine($"[DexaBridge] Ask generic method: {genericMethod}");
            Console.Error.WriteLine($"[DexaBridge] Proxy is null: {proxy == null}");
            Console.Error.WriteLine($"[DexaBridge] Message type: {message?.GetType().FullName}");

            try
            {
                var result = genericMethod.Invoke(proxy, new object[] { message });
                Console.Error.WriteLine($"[DexaBridge] Invoke result: {result?.GetType().FullName ?? "NULL"}");
                return result;
            }
            catch (TargetInvocationException tex)
            {
                Console.Error.WriteLine($"[DexaBridge] Ask invoke error: {tex.InnerException}");
                throw tex.InnerException ?? tex;
            }
        }

        static PipeResponse HandleAsk(PipeRequest request)
        {
            if (DEXActorSystem.CommProxy == null)
                return PipeResponse.Fail(request.Id, "DEXA Server not connected");

            try
            {
                var message = MessageFactory.Create(request.Type, request.Payload);
                Console.Error.WriteLine($"[DexaBridge] Created message: {message.GetType().FullName}");

                var timeout = TimeSpan.FromSeconds(request.TimeoutSeconds ?? 600);
                var replyType = MessageFactory.GetExpectedReplyType(request.Type);
                Console.Error.WriteLine($"[DexaBridge] Expected reply type: {replyType?.FullName ?? "null"}");

                if (_askMethodGeneric == null)
                    FindAskMethod();

                if (_askMethodGeneric == null)
                    return PipeResponse.Fail(request.Id, "CommProxy.Ask method not found");

                // CommProxy.AskServer<T> — 서버로 메시지 전송
                Console.Error.WriteLine($"[DexaBridge] Invoking CommProxy.AskServer<{replyType.Name}>...");

                var taskObj = CallAskServerGeneric(DEXActorSystem.CommProxy, replyType, message);

                if (taskObj == null)
                    return PipeResponse.Fail(request.Id, "CommProxy.Ask returned null task");

                var taskCast = (Task)taskObj;
                if (!taskCast.Wait(timeout))
                    return PipeResponse.Fail(request.Id, $"Timeout after {timeout.TotalSeconds}s");

                if (taskCast.IsFaulted)
                    return PipeResponse.Fail(request.Id, taskCast.Exception?.InnerException?.Message ?? "Ask faulted");

                var result = taskCast.GetType().GetProperty("Result").GetValue(taskCast);

                Console.Error.WriteLine($"[DexaBridge] Ask result: {result?.GetType().Name ?? "null"}");
                return PipeResponse.Ok(request.Id, result?.GetType().Name, result);
            }
            catch (AggregateException aex)
            {
                var inner = aex.InnerException ?? aex;
                Console.Error.WriteLine($"[DexaBridge] Ask error: {inner}");
                return PipeResponse.Fail(request.Id, inner.Message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DexaBridge] Ask error: {ex}");
                return PipeResponse.Fail(request.Id, ex.Message);
            }
        }

        static void HandleTell(PipeRequest request)
        {
            if (DEXActorSystem.CommProxy == null)
                throw new InvalidOperationException("DEXA Server not connected");

            var message = MessageFactory.Create(request.Type, request.Payload);
            // TriggerId 등 핵심 프로퍼티 확인
            var triggerIdProp = message.GetType().GetProperty("TriggerId");
            if (triggerIdProp != null)
                Console.Error.WriteLine($"[DexaBridge] TellServer: {message.GetType().Name} (TriggerId={triggerIdProp.GetValue(message)})");
            else
                Console.Error.WriteLine($"[DexaBridge] TellServer: {message.GetType().Name}");

            DEXActorSystem.CommProxy.TellServer(message);
        }
    }
}
