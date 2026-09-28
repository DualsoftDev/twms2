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

// 자산/폴더 신규 생성.
// ImportAssets 대신 이걸 쓴다 — 서버의 ImportAssets 핸들러는 전체 자산 경로로
// Dictionary 를 만드는데, DEXA 가 중복 경로 폴더를 허용해 만들어 놓은 DB 에서는
// 거기서 그대로 터진다("An item with the same key has already been added.").
// CreateNewAsset 은 중복 검사에 Count() 를 써서 그 영향을 받지 않는다.
// 대신 폴더 자동 생성이 없으므로 상위 폴더를 먼저 만들어야 한다(TypeId=2, 라이선스 검사 면제).
type AmC2SRequestCreateNewAsset(typeId: int, agentPreferences: string, parameter: string, parentId: int) =
    inherit ActorMessage()
    member val TypeId: int = typeId with get, set
    member val ParentId: int = parentId with get, set
    member val AgentPreferences: string = agentPreferences with get, set
    member val Parameter: string = parameter with get, set

    // PLC(.xgwx)/서보(.xpj) 전용. 셋을 채우면 서버가 projectFile 행을 만들고
    // Storage/Project/{id} 에 바이트를 기록한다.
    // 서버는 ProjectModified(protected set) 가 아니라 ProjectPath 유무로 판정하므로
    // 프로퍼티만 채워도 동작한다.
    // 등록 시점에 넣지 않으면 projectFileId 가 NULL 로 남아 **나중에 붙일 수 없다.**
    member val ProjectPath: string = null with get, set
    member val ProjectFileContents: byte array = null with get, set
    member val ProjectFileChecksum: string = null with get, set

    private new() = AmC2SRequestCreateNewAsset(0, null, null, 0)

// 자산/폴더 삭제. 서버는 soft delete(deleted=1) 만 하고 **자식을 따라가지 않는다** —
// 자식 순회는 호출자 책임이고, 부모부터 지우면 고아가 남는다.
// 백업 파일과 이력은 보존되며, 라이선스 슬롯은 반환된다(카운트가 deleted=0 만 센다).
type AmC2SRequestDeleteAssetById(assetId: int) =
    inherit ActorMessage()
    member val AssetId: int = assetId with get, set
    private new() = AmC2SRequestDeleteAssetById(0)
