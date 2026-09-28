namespace DEX.Core.Actor

open System
open DEX.Core.Database.ORM

// ────────────────────────────────────────────────────────────────
// Active — used by Twms2.Server
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

// Single asset backup (AssetExplorer page, fire-and-forget)
type AmC2SRequestExecuteBackupOnce(assetId: int) =
    inherit AmExecuteBackup(assetId, None)

// Connected peers query (ServerConfig page)
type AmC2SRequestConnectedPeers() = inherit ActorMessage()

// Agent restart (ServerConfig page)
type AmC2SRequestAgentRestart() =
    inherit ActorMessage()
    member val Agent: Akka.Actor.IActorRef = null with get, set

// 자산 편집 저장 (이름·설명·IP·에이전트·연결정보 + 프로젝트 파일 교체)
// 서버가 자산명 중복 검사 → AssetConfiguration 플러그인 실행 → DB 반영 →
// 접속 중인 전 클라이언트에 DataChanged 브로드캐스트까지 처리한다.
// 주의: 플러그인이 parameter 를 덧붙일 수 있으므로 저장 후 재조회로 확인해야 한다.
type AmC2SRequestUpdateAssetParameter(viewAsset: ViewAsset) =
    inherit ActorMessage()
    member val ViewAsset: ViewAsset = viewAsset with get, set
    private new() = AmC2SRequestUpdateAssetParameter(Unchecked.defaultof<ViewAsset>)
