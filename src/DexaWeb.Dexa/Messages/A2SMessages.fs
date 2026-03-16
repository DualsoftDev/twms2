namespace DEX.Core.Actor

open System
open DEX.Core.Database.ORM

[<AllowNullLiteral>]
type AmA2SReply(message: obj, ?query: ActorMessage) =
    inherit AmReply(message, defaultArg query null)

    new(query: ActorMessage) = AmA2SReply(null, query)
    internal new() = AmA2SReply((null: obj))

// Server → Agent restart request
type AmS2ARequestAgentRestart() =
    inherit ActorMessage()

// Agent → Server shutdown reply
[<AllowNullLiteral>]
type AmA2SAgentShutdown(query: AmS2ARequestAgentRestart) =
    inherit AmA2SReply(null, query)
    private new() = AmA2SAgentShutdown(Unchecked.defaultof<_>)

[<AllowNullLiteral>]
type AmA2SReplyExecuteBackup(query: ActorMessage,
                              nthSucceeded: Nullable<int>,
                              duration: Nullable<TimeSpan>,
                              contentsChanged: bool,
                              backup: byte array,
                              report: byte array,
                              logs: ActionLog array,
                              result: bool) =
    inherit AmA2SReply(result :> obj, query)

    member val NthSucceeded: Nullable<int> = nthSucceeded with get, set
    member this.Succeeded = this.NthSucceeded.HasValue && this.NthSucceeded.Value >= 0
    member val Duration: Nullable<TimeSpan> = duration with get, set
    member val ContentsChanged: bool = contentsChanged with get, set
    member val Backup: byte array = backup with get, set
    member val Report: byte array = report with get, set
    member val Result: bool = result with get, set
    member val Logs: ActionLog array = logs with get, set
    member val ActionId: int = 0 with get, set

    internal new() =
        AmA2SReplyExecuteBackup(
            Unchecked.defaultof<ActorMessage>,
            Nullable<int>(),
            Nullable<TimeSpan>(),
            false,
            null,
            null,
            null,
            false)

    override this.ToString() =
        let mutable str = sprintf "Result=%b\r\n" this.Succeeded
        if not (isNull this.Logs) then
            for log in this.Logs do
                str <- str + sprintf "%O//%s//%s\r\n" log.DateTime log.Level log.Message
        str
