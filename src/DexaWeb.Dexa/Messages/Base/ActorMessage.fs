namespace DEX.Core.Actor

open System
open DEX.Common
open Newtonsoft.Json

[<AbstractClass; AllowNullLiteral>]
type ActorMessage(guid: Guid, message: obj) =
    member val Guid = guid with get, set
    member val Message = message with get, set
    member val IPAddress = EmDns.getLocalIpAddress().ToString() with get, set

    new(message: obj) = ActorMessage(Guid.NewGuid(), message)

    [<JsonConstructor>]
    new() = ActorMessage(Guid.NewGuid(), null)

    abstract member ToText: unit -> string

    default this.ToText() =
        this.Guid.ToString().[..7]
        + ", IP="
        + this.IPAddress
        + " : "
        + (if isNull this.Message then "" else this.Message.ToString())
        + "@"

    override this.ToString() =
        let text = this.ToText()
        if isNull text then "" else text
