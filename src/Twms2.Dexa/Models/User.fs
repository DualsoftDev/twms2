namespace DEX.Core.Database.ORM

open System

type User() =
    member val Id: Nullable<int> = Nullable<int>() with get, set
    member val UserName: string = null with get, set
    member val Password: string = null with get, set
    member val IsAdmin: bool = false with get, set

    new(userName: string, password: string, isAdmin: bool) as this =
        User()
        then
            this.UserName <- userName
            this.Password <- password
            this.IsAdmin <- isAdmin
