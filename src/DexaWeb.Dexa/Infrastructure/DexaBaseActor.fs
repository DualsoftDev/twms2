namespace DEX.Core.Actor

open System
open Akka.Actor
open Microsoft.Extensions.Logging

[<AbstractClass>]
type DexaBaseActor(logger: ILogger) =
    inherit UntypedActor()

    member _.Logger = logger
    member val Guid = Guid.NewGuid().ToString().ToUpper() with get, set
