namespace DEX.Core.Actor

type AmPong(query: AmPing) =
    inherit AmReply(null, query)

    private new() = AmPong(Unchecked.defaultof<AmPing>)
