namespace DEX.Core.Actor

open System.Collections.Generic
open Akka.Actor
open DEX.Core.Database.ORM

// --- S2C Reply Messages ---

type AmS2CReplyAuthenticateUser(query: AmC2SRequestAuthenticateUser, user: User) =
    inherit AmReply(null, query)
    member val User: User = user with get, set
    member val Status: string = null with get, set
    private new() = AmS2CReplyAuthenticateUser(Unchecked.defaultof<_>, null)

type AmS2CReplyUsers(query: AmC2SRequestUsers, users: User array) =
    inherit AmReply(null, query)
    member val Users: User array = users with get, set
    private new() = AmS2CReplyUsers(Unchecked.defaultof<_>, null)

type AmS2CReplyPermissions(query: AmC2SRequestPermissions, permissions: ViewPermission seq) =
    inherit AmReply(null, query)
    member val Permissions: ViewPermission array = permissions |> Seq.toArray with get, set
    private new() = AmS2CReplyPermissions(Unchecked.defaultof<_>, Seq.empty)

type AmS2CReplyViewAssets(query: AmC2SRequestViewAssets, viewAssets: ViewAsset seq) =
    inherit AmReply(null, query)
    member val ViewAssets: ViewAsset array = viewAssets |> Seq.toArray with get, set
    private new() = AmS2CReplyViewAssets(Unchecked.defaultof<_>, Seq.empty)

type AmS2CReplyAssetTypes(query: AmC2SRequestAssetTypes, assetTypes: AssetType seq) =
    inherit AmReply(null, query)
    member val AssetTypes: AssetType array = assetTypes |> Seq.toArray with get, set
    private new() = AmS2CReplyAssetTypes(Unchecked.defaultof<_>, Seq.empty)

type AmS2CReplyCreateNewAsset(query: AmC2SRequestCreateNewAsset, asset: ViewAsset) =
    inherit AmReply(null, query)
    member val ViewAsset: ViewAsset = asset with get, set
    private new() = AmS2CReplyCreateNewAsset(Unchecked.defaultof<_>, null)

type AmS2CReplyUpdateAssetParameter(query: AmC2SRequestUpdateAssetParameter) =
    inherit AmReply(null, query)
    private new() = AmS2CReplyUpdateAssetParameter(Unchecked.defaultof<_>)

type AmS2CReplyAssetExplorer(req: AmC2SRequestAssetExplorerBase, result: AssetExplorerResult) =
    inherit AmReply(result :> obj, req)
    member val Result: AssetExplorerResult = result with get, set
    new() = AmS2CReplyAssetExplorer(Unchecked.defaultof<_>, null)

type AmS2CReplyDeleteAssets(req: AmC2SRequestDeleteAssets, result: AssetExplorerResult) =
    inherit AmS2CReplyAssetExplorer(req, result)
    private new() = AmS2CReplyDeleteAssets(Unchecked.defaultof<_>, null)

type AmS2CReplyCopyAssetsToFolder(req: AmC2SRequestCopyAssetsToFolder, result: AssetExplorerResult) =
    inherit AmS2CReplyAssetExplorer(req, result)
    private new() = AmS2CReplyCopyAssetsToFolder(Unchecked.defaultof<_>, null)

type AmS2CReplyMoveAssetsToFolder(req: AmC2SRequestMoveAssetsToFolder, result: AssetExplorerResult) =
    inherit AmS2CReplyAssetExplorer(req, result)
    private new() = AmS2CReplyMoveAssetsToFolder(Unchecked.defaultof<_>, null)

type AmS2CReplyTriggers(query: AmC2SRequestTriggers, triggers: Trigger seq) =
    inherit AmReply(null, query)
    member val Triggers: Trigger array = triggers |> Seq.toArray with get, set
    private new() = AmS2CReplyTriggers(Unchecked.defaultof<_>, Seq.empty)

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

type AmS2CReplySchedules(query: AmC2SRequestSchedules, viewSchedules: ViewSchedule seq) =
    inherit AmReply(null, query)
    member val ViewSchedules: ViewSchedule array = viewSchedules |> Seq.toArray with get, set
    private new() = AmS2CReplySchedules(Unchecked.defaultof<_>, Seq.empty)

type AmS2CReplySchedulesChange(query: AmC2SRequestSchedulesChange) =
    inherit AmReply(null, query)
    private new() = AmS2CReplySchedulesChange(Unchecked.defaultof<_>)

type AmS2CReplyExecuteTriggerOnce(query: AmC2SExecuteTriggerOnce, agentReplies: AmReply array) =
    inherit AmReply(null, query)
    member val AgentReplies: AmReply array = agentReplies with get, set
    private new() = AmS2CReplyExecuteTriggerOnce(Unchecked.defaultof<_>, null)

type AmS2CReplyExecuteBackup(query: AmC2SExecuteBackupOnce, agentReply: AmA2SReplyExecuteBackup) =
    inherit AmReply(null, query)
    member val AgentReply: AmA2SReplyExecuteBackup = agentReply with get, set
    member this.ActionId = if isNull this.AgentReply then 0 else this.AgentReply.ActionId
    private new() = AmS2CReplyExecuteBackup(Unchecked.defaultof<_>, null)

type AmS2CReplyBackupFile(query: AmC2SRequestBackupFile, contents: byte array) =
    inherit AmReply(null, query)
    member val Contents: byte array = contents with get, set
    private new() = AmS2CReplyBackupFile(Unchecked.defaultof<_>, null)

type AmS2CReplyDashboardAssetReports(query: AmC2SRequestDashboardAssetReports, assetReports: DashboardAssetReport array) =
    inherit AmReply(null, query)
    member val AssetReports: DashboardAssetReport array = assetReports with get, set
    private new() = AmS2CReplyDashboardAssetReports(Unchecked.defaultof<_>, null)

// Agent shutdown reply (Server → Client)
[<AllowNullLiteral>]
type AmS2CAgentShutdown() =
    inherit AmReply()
    member val AgentReply: AmA2SAgentShutdown = null with get, set

    new(query: AmC2SRequestAgentRestart, agentReply: AmA2SAgentShutdown) as this =
        AmS2CAgentShutdown()
        then this.AgentReply <- agentReply

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

type AmS2CReplyServerLockState(query: AmC2SRequestServerLockState, lc: Locker, errorMessage: string) =
    inherit AmReply(null, query)
    member val Locker: Locker = lc with get, set
    member val ErrorMessage: string = errorMessage with get, set
    private new() = AmS2CReplyServerLockState(Unchecked.defaultof<_>, null, null)

type AmS2CReplyLockServer(query: AmC2SRequestLockServer, locker: Locker) =
    inherit AmReply(null, query)
    member val Locker: Locker = locker with get, set
    member this.Succeeded = not (isNull this.Locker)
    private new() = AmS2CReplyLockServer(Unchecked.defaultof<_>, null)

type AmS2CReplyUnlockServer(query: AmC2SRequestUnlockServer, errorMessage: string) =
    inherit AmReply(null, query)
    member val ErrorMessage: string = errorMessage with get, set
    member this.Succeeded = System.String.IsNullOrEmpty(this.ErrorMessage)
    private new() = AmS2CReplyUnlockServer(Unchecked.defaultof<_>, null)

type AmS2CReplyServerAppConfig(query: AmC2SRequestServerAppConfig, appConfigContents: byte array) =
    inherit AmReply(null, query)
    member val AppConfigContents: byte array = appConfigContents with get, set
    private new() = AmS2CReplyServerAppConfig(Unchecked.defaultof<_>, null)

type AmS2CReplyLicenseInfo(query: AmC2SRequestLicenseInfo) =
    inherit AmReply(null, query)
    member val Info: string = null with get, set
    member val State: int = 0 with get, set
    private new() = AmS2CReplyLicenseInfo(Unchecked.defaultof<_>)

// Subscription reply
[<AllowNullLiteral>]
type AmS2CReplySubscription(query: AmC2SSubscribe,
                             viewAssets: ViewAsset seq,
                             assetTypes: AssetType seq,
                             permissions: ViewPermission seq,
                             locker: Locker,
                             serverVersion: string,
                             isSQLite: bool) =
    inherit AmReplySubscription(query)
    member val AssetTypes: AssetType array = assetTypes |> Seq.toArray with get, set
    member val ViewAssets: ViewAsset array = viewAssets |> Seq.toArray with get, set
    member val Permissions: ViewPermission array = permissions |> Seq.toArray with get, set
    member val Locker: Locker = locker with get, set
    member val ServerVersion: string = serverVersion with get, set
    member val IsSQLiteDatabase: bool = isSQLite with get, set
    private new() = AmS2CReplySubscription(Unchecked.defaultof<_>, Seq.empty, Seq.empty, Seq.empty, null, null, false)

// --- S2X Broadcast Messages ---

type AmS2XNotifyDataChanged(dataChanges: DataChangedNotification array) =
    inherit ActorMessage()
    member val DataChanges: DataChangedNotification array = dataChanges with get, set

type AmS2XServerShutdown(query: AmC2SRequestServerRestart) =
    inherit AmReply(null, query)

// --- S2C Notification Messages ---

type AmS2CServerLockSteteChanged(locker: Locker) =
    inherit ActorMessage()
    member val Locker: Locker = locker with get, set

type AmS2CRefreshMessage() =
    inherit ActorMessage()

type AmS2CReminderMessage(data: List<ReminderData>) =
    inherit ActorMessage()
    member val Data: List<ReminderData> = data with get, set
