namespace DEX.Core.Actor

open System
open DEX.Core.Database.ORM

// ────────────────────────────────────────────────────────────────
// Active — types referenced by used S2C messages
// ────────────────────────────────────────────────────────────────

[<AllowNullLiteral>]
type AmA2SReply(message: obj, ?query: ActorMessage) =
    inherit AmReply(message, defaultArg query null)

    new(query: ActorMessage) = AmA2SReply(null, query)
    internal new() = AmA2SReply((null: obj))

// Server → Agent restart request
type AmS2ARequestAgentRestart() =
    inherit ActorMessage()

// Agent → Server shutdown reply (used by AmS2CAgentShutdown)
[<AllowNullLiteral>]
type AmA2SAgentShutdown(query: AmS2ARequestAgentRestart) =
    inherit AmA2SReply(null, query)
    private new() = AmA2SAgentShutdown(Unchecked.defaultof<_>)
