namespace DEX.Core.Actor

open System
open System.Collections.Generic
open Akka.Actor

type DatabaseChangeOperation =
    | Create = 0
    | Update = 1
    | Delete = 2
    | Unknown = 3

type ReminderData(dt: DateTime, txt: string) =
    member val Notified: bool = false with get, set
    member val Date: DateTime = dt with get, set
    member val Message: string = txt with get, set

[<AllowNullLiteral>]
type DataChangedNotification() =
    let mutable criminalIp = ""
    member _.CriminalIp with get() = criminalIp and set(v) = criminalIp <- v

    new(criminal: IActorRef) as this =
        DataChangedNotification()
        then
            if not (isNull criminal) then
                let criminalType = criminal.GetType()
                let localAddressProp = criminalType.GetProperty("LocalAddressToUse")

                if not (isNull localAddressProp) then
                    let address = localAddressProp.GetValue(criminal)

                    if not (isNull address) then
                        let hostProp = address.GetType().GetProperty("Host")

                        if not (isNull hostProp) then
                            let host = hostProp.GetValue(address)
                            this.CriminalIp <-
                                if isNull host then "" else host.ToString()

    override this.ToString() =
        if System.String.IsNullOrEmpty(this.CriminalIp) then ""
        else "By " + this.CriminalIp

type DatabaseChangedNotification(criminal: IActorRef, tableName: string, databaseChangeOperation: DatabaseChangeOperation) =
    inherit DataChangedNotification(criminal)
    member val TableName: string = tableName with get, set
    member val DatabaseChangeOperation: DatabaseChangeOperation = databaseChangeOperation with get, set

    private new() = DatabaseChangedNotification(null, null, DatabaseChangeOperation.Unknown)

    override this.ToString() =
        sprintf "Table:%s %O %s" this.TableName this.DatabaseChangeOperation (base.ToString())

type ServerLockStatusChangedNotification(locker: Locker) =
    inherit DataChangedNotification()
    member val Locker: Locker = locker with get, set

type RefreshNotification() =
    inherit DataChangedNotification()

type ReminderStatusChangeNotification(data: List<ReminderData>) =
    inherit DataChangedNotification()
    member val Data: List<ReminderData> = data with get, set

type ConnectivityChangedNotification(connected: bool) =
    inherit DataChangedNotification()
    member val Connected: bool = connected with get, set
