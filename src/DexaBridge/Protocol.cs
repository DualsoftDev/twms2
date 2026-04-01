using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Reflection;

namespace DexaBridge
{
    /// <summary>
    /// Named Pipe JSON-line protocol.
    ///
    /// Request (DexaWeb.Server → DexaBridge):
    ///   {"id":"guid","method":"ask","type":"AmC2SExecuteTriggerOnce","payload":{...},"timeout":5}
    ///   {"id":"guid","method":"tell","type":"AmC2SExecuteTriggerOnce","payload":{...}}
    ///   {"id":"guid","method":"ping"}
    ///   {"id":"guid","method":"status"}
    ///
    /// Response (DexaBridge → DexaWeb.Server):
    ///   {"id":"guid","success":true,"type":"AmS2CReplyExecuteTriggerOnce","payload":{...}}
    ///   {"id":"guid","success":false,"error":"timeout"}
    ///
    /// Notification (DexaBridge → DexaWeb.Server, id=null):
    ///   {"id":null,"method":"notify","type":"DataChanged","payload":{...}}
    ///   {"id":null,"method":"notify","type":"Connected","payload":{"connected":true}}
    /// </summary>
    /// <summary>
    /// Akka actor 내부 프로퍼티(Provider, Path 등) 직렬화 시 에러 방지.
    /// IActorRef, IDEXCommProxy 등 직렬화 불가 타입을 건너뜀.
    /// </summary>
    public class SafeContractResolver : DefaultContractResolver
    {
        private static readonly System.Collections.Generic.HashSet<string> SkipTypes = new System.Collections.Generic.HashSet<string>
        {
            "IActorRef", "ActorPath", "Address", "ISurrogated", "Nobody",
            "IDEXCommProxy", "DEXCommProxy", "IActorRefProvider"
        };

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var prop = base.CreateProperty(member, memberSerialization);
            var propTypeName = prop.PropertyType?.Name;

            if (propTypeName != null && (
                SkipTypes.Contains(propTypeName) ||
                propTypeName.Contains("ActorRef") ||
                propTypeName.Contains("CommProxy")))
            {
                prop.ShouldSerialize = _ => false;
            }

            return prop;
        }
    }

    public class PipeRequest
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("method")]
        public string Method { get; set; }  // "ask", "tell", "ping", "status"

        [JsonProperty("type")]
        public string Type { get; set; }    // Message type name (e.g. "AmC2SExecuteTriggerOnce")

        [JsonProperty("payload")]
        public JObject Payload { get; set; }

        [JsonProperty("timeout")]
        public double? TimeoutSeconds { get; set; }
    }

    public class PipeResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("payload")]
        public JToken Payload { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        // Notification-only fields
        [JsonProperty("method", NullValueHandling = NullValueHandling.Ignore)]
        public string Method { get; set; }

        public static PipeResponse Ok(string id, string type, object payload)
        {
            return new PipeResponse
            {
                Id = id,
                Success = true,
                Type = type,
                Payload = payload != null ? JToken.FromObject(payload, JsonSerializer.Create(SerializerSettings)) : null
            };
        }

        public static PipeResponse Fail(string id, string error)
        {
            return new PipeResponse { Id = id, Success = false, Error = error };
        }

        public static PipeResponse Notify(string type, object payload)
        {
            return new PipeResponse
            {
                Id = null,
                Success = true,
                Method = "notify",
                Type = type,
                Payload = payload != null ? JToken.FromObject(payload, JsonSerializer.Create(SerializerSettings)) : null
            };
        }

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Error = (sender, args) => { args.ErrorContext.Handled = true; },
            ContractResolver = new SafeContractResolver()
        };
    }
}
