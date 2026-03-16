namespace DexaWeb.Dexa.Models

/// DeepPinger DLL 에러코드.  C++ DeepPinger/Ping.h 의 #define 상수와 동일하게 유지할 것.
[<AbstractClass; Sealed>]
type DeepPingError private () =

    // Success
    static member val OK = 0

    // Connection stage (1-99): PLC TCP 연결 실패
    static member val WsaStartup = 1
    static member val WinsockVersion = 2
    static member val SocketCreate = 3
    static member val Bind = 4
    static member val IpResolve = 5
    static member val ConnectTimeout = 6
    static member val WsaCreateEvent = 7
    static member val WsaEventSelect = 8

    // Data transfer stage (100-199): 패킷 송수신 실패
    static member val Send = 100
    static member val RecvTimeout = 101

    // Protocol validation stage (200-299): XGT 프로토콜 검증 실패
    static member val PtNullResponse = 200
    static member val PtCompanyMismatch = 201
    static member val PtInvokeMismatch = 202
    static member val PtCommandMismatch = 203
    static member val PtChecksum = 204
    static member val PtPlcError = 205
    static member val PtFrameCmd = 206

    // Diagnostics stage (300-399): 핑 결과 판정 실패
    static member val DiagNull = 300
    static member val DiagPingErrors = 301
    static member val DiagPingNoSuccess = 302

    static member IsSuccess(code: int) = code = 0
    static member IsConnectionError(code: int) = code >= 1 && code <= 99
    static member IsDataTransferError(code: int) = code >= 100 && code <= 199
    static member IsProtocolError(code: int) = code >= 200 && code <= 299
    static member IsDiagnosticsError(code: int) = code >= 300 && code <= 399

    static member GetCategory(code: int) =
        match code with
        | 0 -> "OK"
        | c when c >= 1 && c <= 99 -> "Connection"
        | c when c >= 100 && c <= 199 -> "DataTransfer"
        | c when c >= 200 && c <= 299 -> "Protocol"
        | c when c >= 300 && c <= 399 -> "Diagnostics"
        | _ -> "Unknown"

    static member GetDescription(code: int) =
        match code with
        | 0 -> "성공"
        | 1 -> "WSAStartup 실패"
        | 2 -> "Winsock 버전 불일치"
        | 3 -> "소켓 생성 실패"
        | 4 -> "소켓 바인드 실패"
        | 5 -> "IP 주소 해석 실패"
        | 6 -> "PLC 연결 타임아웃 (포트 2004)"
        | 7 -> "WSACreateEvent 실패"
        | 8 -> "WSAEventSelect 실패"
        | 100 -> "데이터 전송 실패"
        | 101 -> "데이터 수신 타임아웃"
        | 200 -> "Pass-Through 응답 없음"
        | 201 -> "CompanyID 불일치"
        | 202 -> "InvokeID 불일치"
        | 203 -> "Command 불일치"
        | 204 -> "체크섬 검증 실패"
        | 205 -> "PLC Pass-Through 에러"
        | 206 -> "예상치 못한 Frame Command"
        | 300 -> "진단 응답 없음"
        | 301 -> "하부장비 핑 에러"
        | 302 -> "하부장비 핑 성공횟수 불일치"
        | _ -> sprintf "알 수 없는 에러 (%d)" code
