using DEX.Common;
using DEX.Interfaces.Akka;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DexaBridge
{
    public static class MessageFactory
    {
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();
        private static bool _initialized;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            // DEXA 어셈블리들의 모든 타입을 캐시
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var name = asm.GetName().Name;
                    if (name == null) continue;
                    if (!name.StartsWith("DEXA.") && !name.StartsWith("DEX.") && name != "DexaBridge")
                        continue;

                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name.StartsWith("Am") || type.Name.EndsWith("Notification"))
                        {
                            if (!TypeCache.ContainsKey(type.Name))
                                TypeCache[type.Name] = type;
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 로드 가능한 타입만 처리
                    foreach (var t in ex.Types)
                    {
                        if (t != null && (t.Name.StartsWith("Am") || t.Name.EndsWith("Notification")))
                        {
                            if (!TypeCache.ContainsKey(t.Name))
                                TypeCache[t.Name] = t;
                        }
                    }
                }
                catch { }
            }

            Console.Error.WriteLine($"[DexaBridge] MessageFactory: {TypeCache.Count} message types cached");
        }

        // DexaWeb.Dexa 타입명 → DEXA 2.20 실제 타입명 매핑
        private static readonly Dictionary<string, string> TypeAliases = new Dictionary<string, string>
        {
            { "AmC2SExecuteTriggerOnce", "AmC2SExecuteTestEvent" },
            { "AmS2CReplyExecuteTriggerOnce", "AmS2CReplyExecuteTestEvent" },
            { "AmC2SExecuteBackupOnce", "AmC2SRequestExecuteBackupOnce" },
        };

        public static Type FindType(string typeName)
        {
            EnsureInitialized();

            // 별명 매핑
            if (TypeAliases.TryGetValue(typeName, out var alias))
                typeName = alias;

            if (TypeCache.TryGetValue(typeName, out var cached))
                return cached;

            // 못 찾으면 다시 전체 어셈블리 스캔
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType("DEX.Core.Actor." + typeName)
                         ?? asm.GetType("DEX.Common.Akka." + typeName)
                         ?? asm.GetType("DEX.Common." + typeName);
                    if (t != null)
                    {
                        TypeCache[typeName] = t;
                        return t;
                    }
                }
                catch { }
            }

            return null;
        }

        public static object Create(string typeName, JObject payload)
        {
            var type = FindType(typeName);
            if (type == null)
                throw new NotSupportedException($"Unknown message type: {typeName}");

            var proxy = DEXActorSystem.CommProxy;

            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(c => c.GetParameters().Length)
                .ToArray();

            Exception lastError = null;
            foreach (var ctor in ctors)
            {
                try
                {
                    var parameters = ctor.GetParameters();
                    var args = new object[parameters.Length];
                    bool matched = true;

                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var param = parameters[i];
                        var paramType = param.ParameterType;

                        // IDEXCommProxy → CommProxy
                        if (paramType == typeof(IDEXCommProxy) ||
                            paramType.Name.Contains("IDEXCommProxy") ||
                            paramType.Name.Contains("DEXCommProxy") ||
                            (proxy != null && paramType.IsInstanceOfType(proxy)))
                        {
                            args[i] = proxy;
                            continue;
                        }

                        // payload에서 값 찾기
                        if (payload != null && payload.Count > 0)
                        {
                            var token = FindPayloadValue(payload, param.Name);
                            if (token != null)
                            {
                                args[i] = token.ToObject(paramType);
                                continue;
                            }
                        }

                        // 기본값
                        if (param.HasDefaultValue)
                        {
                            args[i] = param.DefaultValue;
                            continue;
                        }

                        // nullable/reference → null
                        if (!paramType.IsValueType || Nullable.GetUnderlyingType(paramType) != null)
                        {
                            args[i] = null;
                            continue;
                        }

                        // value type 기본값
                        args[i] = Activator.CreateInstance(paramType);
                    }

                    if (matched)
                    {
                        var result = ctor.Invoke(args);
                        if (payload != null)
                            ApplyPayloadProperties(result, payload);
                        EnsureSender(result, proxy);
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new NotSupportedException($"Cannot create {typeName}: {lastError?.Message}");
        }

        private static JToken FindPayloadValue(JObject payload, string paramName)
        {
            // 정확한 이름
            var token = payload[paramName];
            if (token != null) return token;

            // 대소문자 무시
            foreach (var prop in payload.Properties())
            {
                if (string.Equals(prop.Name, paramName, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            }

            return null;
        }

        private static void EnsureSender(object obj, object proxy)
        {
            if (obj == null || proxy == null) return;
            try
            {
                var senderProp = obj.GetType().GetProperty("Sender", BindingFlags.Public | BindingFlags.Instance);
                if (senderProp != null && senderProp.CanWrite && senderProp.GetValue(obj) == null
                    && senderProp.PropertyType.IsInstanceOfType(proxy))
                {
                    senderProp.SetValue(obj, proxy);
                }
            }
            catch { }
        }

        private static void ApplyPayloadProperties(object obj, JObject payload)
        {
            if (obj == null || payload == null) return;
            var type = obj.GetType();
            foreach (var prop in payload.Properties())
            {
                try
                {
                    var pi = type.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pi != null && pi.CanWrite)
                    {
                        var val = prop.Value.ToObject(pi.PropertyType);
                        pi.SetValue(obj, val);
                    }
                }
                catch { }
            }
        }

        public static Type GetExpectedReplyType(string requestTypeName)
        {
            // 별명 적용
            if (TypeAliases.TryGetValue(requestTypeName, out var aliased))
                requestTypeName = aliased;

            string replyName = null;
            if (requestTypeName.StartsWith("AmC2SRequest"))
                replyName = "AmS2CReply" + requestTypeName.Substring("AmC2SRequest".Length);
            else if (requestTypeName.StartsWith("AmC2SExecute"))
                replyName = "AmS2CReply" + requestTypeName.Substring("AmC2S".Length);
            else if (requestTypeName == "AmPing")
                replyName = "AmPong";

            if (replyName != null)
            {
                var type = FindType(replyName);
                if (type != null) return type;
                Console.Error.WriteLine($"[DexaBridge] Reply type not found: {replyName}");
            }

            return typeof(object);
        }
    }
}
