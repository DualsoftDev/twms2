namespace DEX.Core.Actor

[<AbstractClass; AllowNullLiteral>]
type AmReply(message: obj, query: ActorMessage) =
    inherit ActorMessage(message)
    member val QueryMessage: ActorMessage = query with get, set

    new() = AmReply(null, null)
