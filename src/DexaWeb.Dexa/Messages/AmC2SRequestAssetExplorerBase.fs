namespace DEX.Core.Actor

[<AbstractClass>]
type AmC2SRequestAssetExplorerBase(assets: string array) =
    inherit ActorMessage()
    member val Assets: string array = assets with get, set

    new() = AmC2SRequestAssetExplorerBase(Array.empty)
