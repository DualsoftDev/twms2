namespace DEX.Core.Actor

open System.Collections.Generic
open DEX.Core.Database.ORM

// ────────────────────────────────────────────────────────────────
// Active — Reply types used by DexaWeb.Server
// ────────────────────────────────────────────────────────────────

// Trigger replies
type AmS2CReplyAddTrigger(query: AmC2SRequestAddTrigger, trigger: Trigger) =
    inherit AmReply(null, query)
    member val Trigger: Trigger = trigger with get, set
    private new() = AmS2CReplyAddTrigger(Unchecked.defaultof<_>, null)

type AmS2CReplyUpdateTrigger(query: AmC2SRequestUpdateTrigger) =
    inherit AmReply(null, query)
    private new() = AmS2CReplyUpdateTrigger(Unchecked.defaultof<_>)

type AmS2CReplyDeleteTrigger(query: AmC2SRequestDeleteTrigger) =
    inherit AmReply(null, query)
    private new() = AmS2CReplyDeleteTrigger(Unchecked.defaultof<_>)

// Connected peers reply (ServerConfig page)
[<AllowNullLiteral>]
type AmS2CReplyConnectedPeers() =
    inherit AmReply()
    member val Peers: ActorInfo array = null with get, set
    member val Summaries: string array = null with get, set

    new(query: AmC2SRequestConnectedPeers, connectedPeers: ActorInfo seq) as this =
        AmS2CReplyConnectedPeers()
        then
            let peerList = connectedPeers |> Seq.toArray
            this.Peers <- peerList
            let summaryList = ResizeArray<string>()
            for peer in peerList do
                let actorRef = peer.ActorRef
                let actorType = peer.ActorType
                summaryList.Add(sprintf "%O %O" actorType actorRef.Path)
            this.Summaries <- summaryList.ToArray()

    override this.ToString() =
        let count = if isNull this.Peers then 0 else this.Peers.Length
        let summaries = if isNull this.Summaries then [||] else this.Summaries
        sprintf "Peers[%d]: %s" count (System.String.Join("\r\n", summaries))

// Agent shutdown reply (ServerConfig page)
[<AllowNullLiteral>]
type AmS2CAgentShutdown() =
    inherit AmReply()
    member val AgentReply: AmA2SAgentShutdown = null with get, set

    new(query: AmC2SRequestAgentRestart, agentReply: AmA2SAgentShutdown) as this =
        AmS2CAgentShutdown()
        then this.AgentReply <- agentReply

// ────────────────────────────────────────────────────────────────
// Broadcast / Notification — received via ServerNotifications
// ────────────────────────────────────────────────────────────────

type AmS2CNotifyDataChanged(dataChanges: DataChangedNotification array) =
    inherit ActorMessage()
    member val DataChanges: DataChangedNotification array = dataChanges with get, set

type AmS2CServerLockSteteChanged(locker: Locker) =
    inherit ActorMessage()
    member val Locker: Locker = locker with get, set

type AmS2CRefreshMessage() =
    inherit ActorMessage()

type AmS2CReminderMessage(data: List<ReminderData>) =
    inherit ActorMessage()
    member val Data: List<ReminderData> = data with get, set
