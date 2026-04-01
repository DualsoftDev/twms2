namespace DEX.Core.Database.ORM

type Storage() =
    member val Id: int = 0 with get, set
    member val ConnectionString: string = null with get, set
    member val StorageType: string = null with get, set
