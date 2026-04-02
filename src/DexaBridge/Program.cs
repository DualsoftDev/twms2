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

            var commProxy = (IDEXCommProxy)proxy;
            try
            {
                commProxy.Initialize(
                    typeof(DummyListenerActor),
                    typeof(DEXClientShortTermActor),
                    typeof(DEX.Client.DEXClientLongTermActor),
                    typeof(DEX.Client.DEXClientStreamActor));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DexaBridge] Initialize error: {ex.GetType().Name}: {ex.Message}");
            }

            proxy.Connect(serverIp, serverPort);
            Console.Error.WriteLine($"[DexaBridge] CommProxy initialized, connecting to {serverIp}:{serverPort}");
        }

        static void FindAskMethod()
        {
            if (DEXActorSystem.CommProxy == null) return;

            var proxyType = DEXActorSystem.CommProxy.GetType();
            var methods = proxyType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Ask" && m.IsGenericMethod)
                .ToArray();

            _askMethodGeneric = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 1 && p[0].ParameterType == typeof(object);
            }) ?? methods.FirstOrDefault();

            if (_askMethodGeneric == null)
                Console.Error.WriteLine("[DexaBridge] Warning: No Ask method found on CommProxy");
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
            Console.Error.WriteLine("[DexaBridge] Waiting for DEXA server connection...");
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
                        Console.Error.WriteLine("[DexaBridge] Server actors ready");
                        if (_askMethodGeneric == null) FindAskMethod();
                        return;
                    }
                }
                catch { }
            }

            Console.Error.WriteLine("[DexaBridge] Warning: Server actors not ready after 30s, continuing anyway");
            if (_askMethodGeneric == null) FindAskMethod();
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
                    .FirstOrDefault(m => m.Name == "AskServer" && m.IsGenericMethod);

                if (_askServerMethodGeneric == null)
                {
                    foreach (var iface in proxy.GetType().GetInterfaces())
                    {
                        _askServerMethodGeneric = iface.GetMethods()
                            .FirstOrDefault(m => m.Name == "AskServer" && m.IsGenericMethod);
                        if (_askServerMethodGeneric != null) break;
                    }
                }
            }

            if (_askServerMethodGeneric == null)
                return CallAskGeneric(proxy, replyType, message);

            return _askServerMethodGeneric.MakeGenericMethod(replyType).Invoke(proxy, new object[] { message });
        }

        static object CallAskGeneric(object proxy, Type replyType, object message)
        {
            if (_askMethodGeneric == null) FindAskMethod();
            if (_askMethodGeneric == null)
                throw new InvalidOperationException("CommProxy.Ask method not found");

            try
            {
                return _askMethodGeneric.MakeGenericMethod(replyType).Invoke(proxy, new object[] { message });
            }
            catch (TargetInvocationException tex)
            {
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
                var timeout = TimeSpan.FromSeconds(request.TimeoutSeconds ?? 600);
                var replyType = MessageFactory.GetExpectedReplyType(request.Type);

                if (_askMethodGeneric == null) FindAskMethod();
                if (_askMethodGeneric == null)
                    return PipeResponse.Fail(request.Id, "CommProxy.Ask method not found");

                var taskObj = CallAskServerGeneric(DEXActorSystem.CommProxy, replyType, message);
                if (taskObj == null)
                    return PipeResponse.Fail(request.Id, "CommProxy.Ask returned null task");

                var taskCast = (Task)taskObj;
                if (!taskCast.Wait(timeout))
                    return PipeResponse.Fail(request.Id, $"Timeout after {timeout.TotalSeconds}s");

                if (taskCast.IsFaulted)
                    return PipeResponse.Fail(request.Id, taskCast.Exception?.InnerException?.Message ?? "Ask faulted");

                var result = taskCast.GetType().GetProperty("Result").GetValue(taskCast);
                return PipeResponse.Ok(request.Id, result?.GetType().Name, result);
            }
            catch (AggregateException aex)
            {
                var inner = aex.InnerException ?? aex;
                Console.Error.WriteLine($"[DexaBridge] Ask error: {inner.Message}");
                return PipeResponse.Fail(request.Id, inner.Message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DexaBridge] Ask error: {ex.Message}");
                return PipeResponse.Fail(request.Id, ex.Message);
            }
        }

        static void HandleTell(PipeRequest request)
        {
            if (DEXActorSystem.CommProxy == null)
                throw new InvalidOperationException("DEXA Server not connected");

            var message = MessageFactory.Create(request.Type, request.Payload);
            DEXActorSystem.CommProxy.TellServer(message);
        }
    }
}
