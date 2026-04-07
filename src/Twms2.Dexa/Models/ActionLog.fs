namespace DEX.Core.Database.ORM

open System

type ActionLog private () =
    member val Id: Nullable<int> = Nullable<int>() with get, set
    member val DateTime: DateTime = DateTime.MinValue with get, set
    member val Level: string = null with get, set
    member val Message: string = null with get, set
    member val ActionId: int = 0 with get, set

    new(dateTime: DateTime, level: string, message: string, actionId: int, ?id: Nullable<int>) as this =
        ActionLog()
        then
            this.Id <- defaultArg id (Nullable<int>())
            this.DateTime <- dateTime
            this.Level <- level
            this.Message <- message
            this.ActionId <- actionId

    override this.ToString() =
        sprintf "%O %s %s" this.DateTime this.Level this.Message
