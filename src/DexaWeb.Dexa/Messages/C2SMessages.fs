namespace DEX.Core.Actor

open System
open DEX.Core.Database.ORM

// ────────────────────────────────────────────────────────────────
// Active — used by DexaWeb.Server
// ────────────────────────────────────────────────────────────────

// Trigger operations
type AmC2SRequestAddTrigger(trigger: Trigger) =
    inherit AmC2STrigger(trigger)

type AmC2SRequestUpdateTrigger(trigger: Trigger) =
    inherit AmC2STrigger(trigger)

type AmC2SRequestDeleteTrigger(trigger: Trigger) =
    inherit AmC2STrigger(trigger)

// Execute trigger (fire-and-forget, long-running backup)
type AmC2SExecuteTriggerOnce(triggerId: int) =
    inherit ActorMessage()
    member val TriggerId: int = triggerId with get, set
    private new() = AmC2SExecuteTriggerOnce(0)

// Data change notification (TWM → DEXA Server → broadcast to clients)
type AmC2SNotifyDataChanged(tableName: string, operation: DatabaseChangeOperation) =
    inherit ActorMessage()
    member val TableName: string = tableName with get, set
    member val Operation: DatabaseChangeOperation = operation with get, set

// Single asset backup (AssetExplorer page, fire-and-forget)
type AmC2SRequestExecuteBackupOnce(assetId: int) =
    inherit AmExecuteBackup(assetId, None)

// Connected peers query (ServerConfig page)
type AmC2SRequestConnectedPeers() = inherit ActorMessage()

// Agent restart (ServerConfig page)
type AmC2SRequestAgentRestart() =
    inherit ActorMessage()
    member val Agent: Akka.Actor.IActorRef = null with get, set
