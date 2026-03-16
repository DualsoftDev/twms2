namespace DexaWeb.Dexa.Infrastructure

open System
open System.Diagnostics
open System.Globalization
open System.Reflection
open System.Text
open Akka.Actor
open Akka.Configuration
open Akka.Serialization
open Akka.Util
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Newtonsoft.Json.Serialization

type private AkkaContractResolver() =
    inherit DefaultContractResolver()

    override _.CreateProperty(``member``: MemberInfo, memberSerialization: MemberSerialization) =
        let prop = base.CreateProperty(``member``, memberSerialization)
        if not prop.Writable then
            match ``member`` with
            | :? PropertyInfo as pi ->
                prop.Writable <- not (isNull (pi.GetSetMethod(true)))
            | _ -> ()
        prop

type DexaJsonSerializer(system: ExtendedActorSystem) =
    inherit SerializerWithStringManifest(system)

    static let namespaceToAssemblyMap =
        dict [
            "DEX.Core.Actor", "DEXA.Core.Actor"
            "DEX.Core.Database.ORM", "DEXA.Core.Database"
            "DEX.Core.Database", "DEXA.Core.Database"
            "DEX.Common.ActorErrors", "DEXA.Common"
            "DEX.Common", "DEXA.Common"
        ]

    let settings =
        let s = JsonSerializerSettings()
        s.PreserveReferencesHandling <- PreserveReferencesHandling.Objects
        s.NullValueHandling <- NullValueHandling.Ignore
        s.DefaultValueHandling <- DefaultValueHandling.Ignore
        s.MissingMemberHandling <- MissingMemberHandling.Ignore
        s.ConstructorHandling <- ConstructorHandling.AllowNonPublicDefaultConstructor
        s.TypeNameHandling <- TypeNameHandling.All
        s.TypeNameAssemblyFormatHandling <- TypeNameAssemblyFormatHandling.Simple
        s.ObjectCreationHandling <- ObjectCreationHandling.Replace
        s.ContractResolver <- AkkaContractResolver()
        s.SerializationBinder <- DexaSerializationBinder()
        s

    let serializer = JsonSerializer.Create(settings)

    do
        // Add SurrogateConverter after serializer is created
        settings.Converters.Add(DexaJsonSerializer.SurrogateConverterInstance(system, serializer))

    new(system: ExtendedActorSystem, _config: Config) = DexaJsonSerializer(system)

    override _.Manifest(o: obj) =
        let t = o.GetType()
        let typeName = t.FullName
        if not (isNull typeName) then
            let mutable found = false
            let mutable result = ""
            // Sort by key length descending to match longest prefix first
            let sorted =
                namespaceToAssemblyMap
                |> Seq.sortByDescending (fun kvp -> kvp.Key.Length)
            for kvp in sorted do
                if not found && typeName.StartsWith(kvp.Key) then
                    found <- true
                    result <- sprintf "%s, %s" typeName kvp.Value
            if found then result
            else sprintf "%s, %s" t.FullName (t.Assembly.GetName().Name)
        else
            sprintf "%s, %s" t.FullName (t.Assembly.GetName().Name)

    override _.ToBinary(obj: obj) =
        let json = JsonConvert.SerializeObject(obj, settings)
        Encoding.UTF8.GetBytes(json)

    override _.FromBinary(bytes: byte array, manifest: string) =
        let json = Encoding.UTF8.GetString(bytes)
        if String.IsNullOrEmpty(manifest) then
            JsonConvert.DeserializeObject(json, settings)
        else
            let t = DexaJsonSerializer.ResolveType(manifest)
            JsonConvert.DeserializeObject(json, t, settings)

    static member private ResolveType(manifest: string) =
        let t = Type.GetType(manifest)
        if not (isNull t) then t
        else
            let commaIndex = manifest.IndexOf(',')
            if commaIndex > 0 then
                let typeName = manifest.[..commaIndex - 1].Trim()
                let ourType = typeof<DexaJsonSerializer>.Assembly.GetType(typeName)
                if not (isNull ourType) then ourType
                else
                    let mutable found: Type = null
                    for asm in AppDomain.CurrentDomain.GetAssemblies() do
                        if isNull found then
                            let f = asm.GetType(typeName)
                            if not (isNull f) then found <- f
                    if isNull found then
                        Debug.WriteLine(sprintf "[DexaSerializer] Cannot resolve manifest: '%s'" manifest)
                        typeof<obj>
                    else
                        found
            else
                Debug.WriteLine(sprintf "[DexaSerializer] Cannot resolve manifest: '%s'" manifest)
                typeof<obj>

    static member private SurrogateConverterInstance(system: ExtendedActorSystem, serializer: JsonSerializer) =
        { new JsonConverter() with
            override _.CanConvert(objectType: Type) =
                objectType = typeof<int>
                || objectType = typeof<float32>
                || objectType = typeof<decimal>
                || typeof<ISurrogated>.IsAssignableFrom(objectType)
                || objectType = typeof<obj>

            override _.WriteJson(writer: JsonWriter, value: obj, ser: JsonSerializer) =
                match value with
                | :? int | :? decimal | :? float32 ->
                    writer.WriteStartObject()
                    writer.WritePropertyName("$")
                    writer.WriteValue(DexaJsonSerializer.GetString(value) : string)
                    writer.WriteEndObject()
                | :? ISurrogated as surrogated ->
                    let surrogate = surrogated.ToSurrogate(system)
                    ser.Serialize(writer, surrogate)
                | _ ->
                    ser.Serialize(writer, value)

            override _.ReadJson(reader: JsonReader, objectType: Type, _existingValue: obj, ser: JsonSerializer) =
                let surrogate = ser.Deserialize(reader)
                DexaJsonSerializer.TranslateSurrogate(surrogate, objectType, system, serializer)
        }

    static member private GetString(value: obj) =
        match value with
        | :? int as i -> "I" + i.ToString(NumberFormatInfo.InvariantInfo)
        | :? float32 as f -> "F" + f.ToString(NumberFormatInfo.InvariantInfo)
        | :? decimal as d -> "M" + d.ToString(NumberFormatInfo.InvariantInfo)
        | _ -> raise (NotSupportedException())

    static member private GetValue(v: string) : obj =
        let t = v.[..0]
        let s = v.[1..]
        match t with
        | "I" -> int (Int32.Parse(s, NumberFormatInfo.InvariantInfo)) :> obj
        | "F" -> float32 (Single.Parse(s, NumberFormatInfo.InvariantInfo)) :> obj
        | "M" -> decimal (Decimal.Parse(s, NumberFormatInfo.InvariantInfo)) :> obj
        | _ -> raise (NotSupportedException())

    static member private TranslateSurrogate(deserializedValue: obj, objectType: Type, system: ExtendedActorSystem, serializer: JsonSerializer) : obj =
        match deserializedValue with
        | :? JObject as j ->
            let dollarToken = j.["$"]
            if not (isNull dollarToken) then
                DexaJsonSerializer.GetValue(dollarToken.Value<string>())
            else
                j.ToObject(objectType, serializer)
        | :? ISurrogate as surrogate ->
            surrogate.FromSurrogate(system)
        | _ ->
            deserializedValue
