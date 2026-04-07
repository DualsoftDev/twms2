namespace DEX.Core.Actor

open System
open Akka.Actor
open DEX.Common
open DEX.Common.Core

type AgentInfo() =
    inherit ActorInfo()

    member val TableId: Nullable<int> = Nullable() with get, set

    [<Newtonsoft.Json.JsonProperty>]
    member val HardwarePerformance: HardwarePerformance = null with get, set

    [<Newtonsoft.Json.JsonProperty>]
    member val HardwarePerformanceTimeStamp: DateTime = DateTime.MinValue with get, set

    new(actor: IActorRef,
        name: string,
        swVersion: string,
        assemblies: string,
        ipAddress: string,
        hwPerformance: HardwarePerformance,
        processId: int) as this =
        AgentInfo()
        then
            this.Name <- name
            this.ActorRef <- actor
            this.ActorType <- ActorType.Agent
            this.SwVersion <- swVersion
            this.Assemblies <- assemblies
            this.Ip <- ipAddress
            this.Pid <- processId
            this.HardwarePerformance <- hwPerformance

    override this.ToString() =
        sprintf "%s %s" (base.ToString()) this.SwVersion
