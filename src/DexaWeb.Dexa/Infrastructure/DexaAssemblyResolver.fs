namespace DexaWeb.Dexa.Infrastructure

open System
open System.Reflection

[<AbstractClass; Sealed>]
type DexaAssemblyResolver private () =
    static let ourAssembly = typeof<DexaAssemblyResolver>.Assembly
    static let mutable registered = false

    static let knownDexaAssemblies =
        set [
            "DEXA.Core.Actor"
            "DEXA.Common.Akka"
            "DEXA.Core.Database"
            "DEXA.Common"
            "DEXA.Common.Core"
            "DEXA.Client"
            "DEXA.Interfaces"
        ]

    static member Register() =
        if not registered then
            registered <- true
            AppDomain.CurrentDomain.add_AssemblyResolve(ResolveEventHandler(fun _ args ->
                let name = AssemblyName(args.Name)
                if not (isNull name.Name) && knownDexaAssemblies.Contains(name.Name) then ourAssembly
                else null))
