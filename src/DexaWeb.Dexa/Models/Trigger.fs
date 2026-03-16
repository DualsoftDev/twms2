namespace DEX.Core.Database.ORM

open System
open System.Runtime.InteropServices

type Trigger() =
    member val Id: Nullable<int> = Nullable<int>() with get, set
    member val Name: string = null with get, set
    member val CronSpec: string = null with get, set
    member val Description: string = null with get, set
    member val Deleted: bool = false with get, set
    member val Enable: bool = false with get, set

    new(id: Nullable<int>,
        name: string,
        cronSpec: string,
        description: string,
        [<Optional; DefaultParameterValue(false)>] deleted: bool,
        [<Optional; DefaultParameterValue(true)>] enable: bool) as this =
        Trigger()
        then
            this.Id <- id
            this.Name <- name
            this.CronSpec <- cronSpec
            this.Description <- description
            this.Deleted <- deleted
            this.Enable <- enable
