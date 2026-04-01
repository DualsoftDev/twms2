namespace DEX.Core.Actor

open System

[<AbstractClass; Sealed>]
type DEXActorGlobal private () =
    static let mutable backupServerActorSystemName = "server-system"
    static member BackupServerActorSystemName with get() = backupServerActorSystemName and set(v) = backupServerActorSystemName <- v
    static member CryptKey = "lse"
    static member ClientAtorSystemName = "client-system"
    static member AgentAtorSystemName = "agent-system"
    static member CreateAgentActorSystemName() =
        DEXActorGlobal.AgentAtorSystemName + "-" + Guid.NewGuid().ToString().[..7]
    static member GuardianActorName = "guardian-actor"
    /// DEXA 서버의 CommProxy.Name (actor 이름 prefix)
    static member ServerName = "DEXServer"
    /// 클라이언트 Akka 포트. 0 = OS가 빈 포트 자동 할당 (기존 DEXA Client와 공존 가능)
    static member ClientPort = 0
