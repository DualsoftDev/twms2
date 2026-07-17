namespace Twms2.Dexa

open System.Reflection
open HarmonyLib
open Microsoft.Extensions.Logging

/// DEXA DLL의 클라이언트 Akka 포트 하드코딩(50011) 런타임 패치.
///
/// DEXA.Interfaces의 DEX.Interfaces.Global.DEXActorGlobal.ActorSystemPort()는
/// Server=50001 / Client=50011 / Agent=50021 을 컴파일 상수(switch)로 반환하며 설정을 읽지 않는다.
/// 같은 PC에서 클라이언트형 프로세스 2개(TWMS + DEXA.ClientApp GUI)가 모두 50011을 쓰면
/// 서버 입장에서 동일 주소(client-system@호스트:50011)라 한 쪽만 연결된다.
/// Harmony Postfix로 Client 타입의 반환값만 설정 포트로 교체한다 (0 = OS 랜덤 할당).
module DexaPortPatch =

    let mutable private applied = false
    let mutable private clientPort = 0

    /// Harmony Postfix 대상. 파라미터 이름은 원본 메서드와 일치해야 한다:
    /// ActorSystemPort(ActorType type) → type / __result(반환값 주입)
    type PatchHolder() =
        static member Postfix(``type``: DEX.Interfaces.Akka.ActorType, __result: int byref) =
            if ``type`` = DEX.Interfaces.Akka.ActorType.Client then
                __result <- clientPort

    /// 패치 적용. CommProxy.Initialize(ActorSystem 생성) 이전에 호출해야 한다.
    /// port = 50011 이면 DEXA 기본 동작 그대로 두고 패치하지 않는다.
    let apply (port: int) (logger: ILogger) =
        if applied then true
        elif port = 50011 then
            logger.LogInformation("DEXA 클라이언트 포트 패치 생략 (50011 = DEXA 기본값 유지)")
            false
        else
            try
                // DEXActorGlobal은 static class여서 typeof<> 불가 → 어셈블리에서 조회
                let asm = typeof<DEX.Interfaces.Akka.ActorType>.Assembly
                let target = asm.GetType("DEX.Interfaces.Global.DEXActorGlobal")
                let original = target.GetMethod("ActorSystemPort", BindingFlags.Public ||| BindingFlags.Static)
                let postfix = typeof<PatchHolder>.GetMethod("Postfix", BindingFlags.Public ||| BindingFlags.Static)
                clientPort <- port
                let harmony = Harmony("twms2.dexa.client-port")
                harmony.Patch(original, postfix = HarmonyMethod(postfix)) |> ignore
                applied <- true
                logger.LogInformation(
                    "DEXA 클라이언트 Akka 포트 패치 적용: 50011 → {Port}",
                    (if port = 0 then "랜덤(OS 할당)" else string port) :> obj)
                true
            with ex ->
                logger.LogWarning(ex, "DEXA 클라이언트 포트 패치 실패 — 기본 50011로 동작 (DEXA.ClientApp와 동시 실행 불가)")
                false
