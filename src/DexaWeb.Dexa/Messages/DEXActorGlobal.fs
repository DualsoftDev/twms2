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
