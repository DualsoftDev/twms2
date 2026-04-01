namespace DexaWeb.Dexa.Infrastructure

open System
open System.Diagnostics
open System.Reflection
open Newtonsoft.Json.Serialization

type DexaSerializationBinder() =
    static let ourAssembly = typeof<DexaSerializationBinder>.Assembly

    static let knownDexaAssemblies =
        set [
            "DEXA.Core.Actor"
            "DEXA.Common.Akka"
            "DEXA.Core.Database"
            "DEXA.Common"
            "DEXA.Common.Core"
        ]

    static let namespaceToAssemblyMap =
        dict [
            "DEX.Core.Actor", "DEXA.Core.Actor"
            "DEX.Core.Database.ORM", "DEXA.Core.Database"
            "DEX.Core.Database", "DEXA.Core.Database"
            "DEX.Common.ActorErrors", "DEXA.Common"
            "DEX.Common", "DEXA.Common"
        ]

    interface ISerializationBinder with
        member _.BindToName(serializedType: Type, assemblyName: string byref, typeName: string byref) =
            typeName <- serializedType.FullName

            if serializedType.Assembly = ourAssembly && not (isNull typeName) then
                let mutable found = false
                for kvp in namespaceToAssemblyMap do
                    if not found && typeName.StartsWith(kvp.Key) then
                        assemblyName <- kvp.Value
                        found <- true
                if not found then
                    assemblyName <- serializedType.Assembly.GetName().Name
                Debug.WriteLine(sprintf "[DexaBinder] BindToName: %s → %s" typeName assemblyName)
            else
                assemblyName <- serializedType.Assembly.GetName().Name

        member _.BindToType(assemblyName: string, typeName: string) : Type =
            if not (isNull assemblyName) && knownDexaAssemblies.Contains(assemblyName) then
                let t = ourAssembly.GetType(typeName)
                if not (isNull t) then t
                else
                    DexaSerializationBinder.FallbackResolve(assemblyName, typeName)
            else
                DexaSerializationBinder.FallbackResolve(assemblyName, typeName)

    static member private FallbackResolve(assemblyName: string, typeName: string) : Type =
        if not (isNull assemblyName) then
            let qualifiedName = sprintf "%s, %s" typeName assemblyName
            let t = Type.GetType(qualifiedName)
            if not (isNull t) then t
            else
                DexaSerializationBinder.SearchAllAssemblies(typeName)
        else
            DexaSerializationBinder.SearchAllAssemblies(typeName)

    static member private SearchAllAssemblies(typeName: string) : Type =
        let fallbackType = Type.GetType(typeName)
        if not (isNull fallbackType) then fallbackType
        else
            let mutable found: Type = null
            for assembly in AppDomain.CurrentDomain.GetAssemblies() do
                if isNull found then
                    let t = assembly.GetType(typeName)
                    if not (isNull t) then found <- t
            if isNull found then
                Debug.WriteLine(sprintf "[DexaBinder] Unknown type: %s" typeName)
                typeof<obj>
            else
                found
