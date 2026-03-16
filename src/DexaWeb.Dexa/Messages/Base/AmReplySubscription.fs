namespace DEX.Core.Actor

[<AllowNullLiteral>]
type AmReplySubscription(message: obj, query: AmSubscribe) =
    inherit AmReply(message, query)

    new(query: AmSubscribe) = AmReplySubscription(null, query)

    new() = AmReplySubscription(null, null)
