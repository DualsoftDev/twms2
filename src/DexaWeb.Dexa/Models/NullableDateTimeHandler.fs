namespace DEX.Core.Database

open System
open System.Data
open Dapper

type NullableDateTimeHandler() =
    inherit SqlMapper.TypeHandler<Nullable<DateTime>>()

    static member val Default = NullableDateTimeHandler()

    override _.SetValue(parameter: IDbDataParameter, value: Nullable<DateTime>) =
        parameter.Value <-
            if value.HasValue then value.Value :> obj
            else DBNull.Value :> obj

    override _.Parse(value: obj) =
        match value with
        | null -> Nullable<DateTime>()
        | :? DateTime as dt -> Nullable<DateTime>(dt)
        | v -> Nullable<DateTime>(Convert.ToDateTime(v))
