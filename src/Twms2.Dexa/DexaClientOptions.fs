namespace Twms2.Dexa

type DexaClientOptions() =
    member val ServerIp: string = "127.0.0.1" with get, set
    member val ServerPort: int = 9090 with get, set
    member val AskTimeoutSeconds: int = 30 with get, set
    member val PingTimeoutSeconds: int = 5 with get, set
    member val ClientName: string = "twm-web" with get, set
    member val DllPath: string = @"C:\Program Files (x86)\LS\DEXA\Client" with get, set
    /// 클라이언트 Akka 수신 포트. DEXA DLL은 50011을 하드코딩하고 있어 DEXA.ClientApp(GUI)와
    /// 동시 실행 시 충돌한다. 0 = OS 랜덤 할당(GUI와 공존), 50011 = 패치 없이 DEXA 기본 동작.
    member val ClientPort: int = 0 with get, set
