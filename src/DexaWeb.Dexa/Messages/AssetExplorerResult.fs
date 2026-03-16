namespace DEX.Core.Actor

[<AllowNullLiteral>]
type AssetExplorerResult() =
    member val Errors: string array = null with get, set
    member this.Succeeded = isNull this.Errors || this.Errors.Length = 0

    new(errors: string seq) as this =
        AssetExplorerResult()
        then
            if not (isNull errors) then
                this.Errors <- errors |> Seq.toArray
