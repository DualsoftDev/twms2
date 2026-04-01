namespace DEX.Core.Database.ORM

open System

type AssetType() =
    member val Id: Nullable<int> = Nullable<int>() with get, set
    member val Guid: string = null with get, set
    member val Fake: bool = false with get, set
    member val UserFriendlyName: string = null with get, set
    member val Parameter: string = null with get, set
    member val Icon: string = null with get, set
    member val DotnetClassName: string = null with get, set
    member val Description: string = null with get, set

    new(id: Nullable<int>,
        guid: string,
        userFriendlyName: string,
        parameter: string,
        icon: string,
        dotnetClassName: string) as this =
        AssetType()
        then
            this.Id <- id
            this.Guid <- guid
            this.UserFriendlyName <- userFriendlyName
            this.Parameter <- parameter
            this.Icon <- icon
            this.DotnetClassName <- dotnetClassName
