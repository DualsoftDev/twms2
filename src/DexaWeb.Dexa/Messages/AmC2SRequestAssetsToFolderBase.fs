namespace DEX.Core.Actor

[<AbstractClass>]
type AmC2SRequestAssetsToFolderBase(sources: string array, folder: string) =
    inherit AmC2SRequestAssetExplorerBase(sources)
    member val Folder: string = folder with get, set

    new() = AmC2SRequestAssetsToFolderBase(Array.empty, null)
