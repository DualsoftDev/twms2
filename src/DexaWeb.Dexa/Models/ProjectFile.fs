namespace DEX.Core.Database.ORM

open System

type ProjectFile() =
    member val Id: Nullable<int> = Nullable<int>() with get, set
    member val Path: string = null with get, set
    member val Checksum: string = null with get, set
    member val FileContents: byte array = null with get, set
    member val ModifiedByClient: bool = false with get, set

    new(id: Nullable<int>, path: string, checksum: string) as this =
        ProjectFile()
        then
            this.Id <- id
            this.Path <- path
            this.Checksum <- checksum
