namespace Twms2.Dexa

open System
open System.Threading.Tasks

/// DEXA 서버가 요청을 거부했을 때 던진다(DEXResponseError / AmReplyError).
/// 서버가 보낸 사유를 그대로 담는다 — 라이선스 만료, 자산명 중복, 서버 Lock 등.
/// 이 예외가 없으면 거부가 캐스팅 실패나 타임아웃으로 둔갑해 원인을 잃는다.
type DexaServerException(requestType: string, message: string) =
    inherit Exception(message)
    member _.RequestType = requestType

type IDexaClient =
    inherit IDisposable
    abstract IsConnected: bool
    abstract PingServerAsync: unit -> Task<bool>
    abstract InitializeAsync: unit -> Task
    abstract AskServerAsync<'T> : message: obj * ?timeout: TimeSpan -> Task<'T>
    abstract TellServer: message: obj -> unit
    abstract ServerNotifications: IObservable<obj>
