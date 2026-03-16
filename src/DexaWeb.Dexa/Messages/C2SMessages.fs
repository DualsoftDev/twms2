namespace DEX.Core.Actor

open System
open DEX.Core.Database.ORM

// ────────────────────────────────────────────────────────────────
// Active — used by DexaWeb.Server
// ────────────────────────────────────────────────────────────────

// Simple request markers (active)
type AmC2SRequestViewAssets() = inherit ActorMessage()
type AmC2SRequestConnectedPeers() = inherit ActorMessage()
type AmC2SRequestServerAppConfig() = inherit ActorMessage()

// Authentication
type AmC2SRequestAuthenticateUser(userName: string, password: string) =
    inherit ActorMessage()
    member val UserName: string = userName with get, set
    member val Password: string = password with get, set

// Asset operations
type AmC2SRequestCreateNewAsset(assetTypeId: int,
                                 agentPreferences: string,
                                 parameter: string,
                                 parentId: int) =
    inherit ActorMessage()
    member val TypeId: int = assetTypeId with get, set
    member val ParentId: int = parentId with get, set
    member val AgentPreferences: string = agentPreferences with get, set
    member val Parameter: string = parameter with get, set
    member val ProjectModified: bool = false with get, set
    member val ProjectPath: string = null with get, set
    member val ProjectFileContents: byte array = null with get, set
    member val ProjectFileChecksum: string = null with get, set

    new(assetTypeId: int,
        agentPreferences: string,
        parameter: string,
        parentId: int,
        projectPath: string,
        projectFileContents: byte array,
        projectFileChecksum: string) as this =
        AmC2SRequestCreateNewAsset(assetTypeId, agentPreferences, parameter, parentId)
        then
            this.ProjectModified <- true
            this.ProjectPath <- projectPath
            this.ProjectFileContents <- projectFileContents
            this.ProjectFileChecksum <- projectFileChecksum

    private new() = AmC2SRequestCreateNewAsset(0, null, null, 0)

type AmC2SRequestUpdateAssetParameter(viewAsset: ViewAsset) =
    inherit ActorMessage()
    member val ViewAsset: ViewAsset = viewAsset with get, set
    private new() = AmC2SRequestUpdateAssetParameter(null)

// Trigger operations
type AmC2SRequestAddTrigger(trigger: Trigger) =
    inherit AmC2STrigger(trigger)

type AmC2SRequestUpdateTrigger(trigger: Trigger) =
    inherit AmC2STrigger(trigger)

type AmC2SRequestDeleteTrigger(trigger: Trigger) =
    inherit AmC2STrigger(trigger)

// Schedule operations
type AmC2SRequestSchedulesChange(adds: Tuple<int, int> seq, removes: Tuple<int, int> seq) =
    inherit ActorMessage()
    member val Adds: Tuple<int, int> array = adds |> Seq.toArray with get, set
    member val Removes: Tuple<int, int> array = removes |> Seq.toArray with get, set
    private new() = AmC2SRequestSchedulesChange(Seq.empty, Seq.empty)

// Execute operations
type AmC2SExecuteTriggerOnce(triggerId: int) =
    inherit ActorMessage()
    member val TriggerId: int = triggerId with get, set
    private new() = AmC2SExecuteTriggerOnce(0)

// Agent restart
type AmC2SRequestAgentRestart() =
    inherit ActorMessage()
    member val Agent: Akka.Actor.IActorRef = null with get, set

// Data change notification (TWM → DEXA Server → broadcast to clients)
type AmC2SNotifyDataChanged(tableName: string, operation: DatabaseChangeOperation) =
    inherit ActorMessage()
    member val TableName: string = tableName with get, set
    member val Operation: DatabaseChangeOperation = operation with get, set

// ────────────────────────────────────────────────────────────────
// Reserved — DEXA protocol definitions, not yet used by Server
// ────────────────────────────────────────────────────────────────

// Simple request markers (reserved)
type AmC2SRequestUsers() = inherit ActorMessage()
type AmC2SRequestPermissions() = inherit ActorMessage()
type AmC2SRequestAssetTypes() = inherit ActorMessage()
type AmC2SRequestTriggers() = inherit ActorMessage()
type AmC2SRequestSchedules() = inherit ActorMessage()
type AmC2SRequestServerLockState() = inherit ActorMessage()
type AmC2SRequestLicenseInfo() = inherit ActorMessage()
type AmC2SRequestServerRestart() = inherit ActorMessage()

// Asset explorer operations
type AmC2SRequestDeleteAssets(assets: string array) =
    inherit AmC2SRequestAssetExplorerBase(assets)

type AmC2SRequestCopyAssetsToFolder(sources: string array, folder: string) =
    inherit AmC2SRequestAssetsToFolderBase(sources, folder)

type AmC2SRequestMoveAssetsToFolder(sources: string array, folder: string) =
    inherit AmC2SRequestAssetsToFolderBase(sources, folder)

// Execute operations (reserved)
type AmC2SExecuteBackupOnce(assetId: int) =
    inherit AmExecuteBackup(assetId, None)

type AmC2SRequestBackupFile(actionId: int, isBackup: bool) =
    inherit ActorMessage()
    member val ActionId: int = actionId with get, set
    member val IsBackup: bool = isBackup with get, set

type AmC2SRequestDashboardAssetReports(start: Nullable<DateTime>, ``end``: Nullable<DateTime>, normalUid: int option) =
    inherit ActorMessage()
    member val NormalUserId: int option = normalUid with get, set
    member val Start: Nullable<DateTime> = start with get, set
    member val End: Nullable<DateTime> = ``end`` with get, set

// Lock operations
type AmC2SRequestLockServer(user: string) =
    inherit ActorMessage()
    member val User: string = user with get, set

type AmC2SRequestUnlockServer(user: string) =
    inherit ActorMessage()
    member val User: string = user with get, set

// Subscribe
[<AllowNullLiteral>]
type AmC2SSubscribe(actorInfo: ActorInfo) =
    inherit AmSubscribe()
    member val ActorInfo: ActorInfo = actorInfo with get, set
    private new() = AmC2SSubscribe(null)
