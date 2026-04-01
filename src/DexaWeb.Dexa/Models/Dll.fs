namespace DEX.Core.Database.ORM

type Dll() =
    member val Id: int = 0 with get, set
    member val Name: string = null with get, set
    member val Version: string = null with get, set
    member val Location: string = null with get, set
    member val Size: int = 0 with get, set
    member val Checksum: string = null with get, set
