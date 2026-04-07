namespace DEX.Core.Actor

open System
open Microsoft.Extensions.Logging

type AmReplyError(exceptionMessage: string, query: ActorMessage) =
    inherit AmReply(exceptionMessage :> obj, query)
    member val DetailExceptionMessage: string = null with get, set

    new(ex: Exception, query: ActorMessage) as this =
        AmReplyError(ex.Message, query)
        then this.DetailExceptionMessage <- ex.Message

    internal new() = AmReplyError((null: string), (null: ActorMessage))

    member this.ExceptionMessage: string = this.Message :?> string

    static member Create(ex: Exception, query: ActorMessage, logger: ILogger, ?context: string) =
        logger.LogError(
            "Exception occurred processing {Context} {QueryType}: {Exception}",
            (defaultArg context null),
            query.GetType().Name,
            ex.ToString())
        AmReplyError(ex, query)

    override this.ToText() = sprintf "ERROR : %O" this.Message
