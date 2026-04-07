namespace Twms2.Dexa

type DexaClientOptions() =
    member val ServerIp: string = "127.0.0.1" with get, set
    member val ServerPort: int = 9090 with get, set
    member val AskTimeoutSeconds: int = 30 with get, set
    member val PingTimeoutSeconds: int = 5 with get, set
    member val ClientName: string = "twm-web" with get, set
    member val DllPath: string = @"C:\Program Files (x86)\LS\DEXA\Client" with get, set
