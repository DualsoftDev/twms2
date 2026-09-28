namespace DEX.Core.Actor

open System.Collections.Generic
open DEX.Core.Database.ORM

// ────────────────────────────────────────────────────────────────
// Active — Reply types used by Twms2.Server
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

// 자산 편집 저장 응답
// ViewAsset 은 DEXA 타입이라 래퍼로 복사되지 않는다(타입 불일치) — 값은 DB 재조회로 확인할 것.
type AmS2CReplyUpdateAssetParameter(query: AmC2SRequestUpdateAssetParameter, viewAsset: ViewAsset) =
    inherit AmReply(null, query)
    member val ViewAsset: ViewAsset = viewAsset with get, set
    private new() = AmS2CReplyUpdateAssetParameter(Unchecked.defaultof<_>, Unchecked.defaultof<_>)

// 자산 생성 응답. ViewAsset 은 DEXA 타입이라 래퍼로 복사되지 않으므로
// 생성된 ID 는 DB 재조회로 얻어야 한다.
[<AllowNullLiteral>]
type AmS2CReplyRegisterAsset() =
    inherit AmReply()

// 자산 삭제 응답. 서버가 내부 예외를 삼키고도 이 응답을 보내므로
// 실제 삭제 여부는 DB 재조회로 확인해야 한다.
[<AllowNullLiteral>]
type AmS2CReplyDeleteAsset() =
    inherit AmReply()

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
