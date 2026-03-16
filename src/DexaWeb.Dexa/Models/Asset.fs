namespace DEX.Core.Database.ORM

open System
open System.Linq

type Asset() =
    let mutable name: string = null
    let mutable parameter: string = null

    member _.Name = name
    member val Id: int = 0 with get, set
    member val ParentId: int = 0 with get, set
    member val AssetTypeId: Nullable<int> = Nullable<int>() with get, set

    member _.Parameter
        with get () = parameter
        and set (v) =
            parameter <- v
            name <- Asset.ExtractAssetName(v)

    member val Deleted: bool = false with get, set
    member val ProjectFileDirty: bool = false with get, set
    member val ProjectFileId: Nullable<int> = Nullable<int>() with get, set
    member val StorageId: Nullable<int> = Nullable<int>() with get, set
    member val AgentPreferences: string = null with get, set

    member _.Description = ""

    member this.GetPreferredAgentNames() : string seq =
        if System.String.IsNullOrEmpty(this.AgentPreferences) then
            Enumerable.Empty<string>()
        else
            this.AgentPreferences.Split(';') :> string seq

    member this.IsSystemRootFolderAsset =
        this.AssetTypeId.HasValue && (if this.AssetTypeId.HasValue then this.AssetTypeId.Value else 0) = 1

    member this.IsNonRootFolderAsset =
        this.AssetTypeId.HasValue && (if this.AssetTypeId.HasValue then this.AssetTypeId.Value else 0) = 2

    member this.IsFolderAsset =
        this.IsSystemRootFolderAsset || this.IsNonRootFolderAsset

    member this.IsRealAsset = not this.IsFolderAsset

    static member private ExtractAssetName(p: string) : string = p

    new(id: int,
        parentId: int,
        assetTypeId: Nullable<int>,
        agentPreferences: string,
        parameter: string,
        projectFileId: Nullable<int>,
        ?projectFileDirty: bool,
        ?deleted: bool,
        ?storageId: Nullable<int>) as this =
        Asset()
        then
            this.Id <- id
            this.ParentId <- parentId
            this.AssetTypeId <- assetTypeId
            this.Parameter <- parameter
            this.Deleted <- defaultArg deleted false
            this.ProjectFileId <- projectFileId
            this.StorageId <- defaultArg storageId (Nullable<int>())
            this.AgentPreferences <- agentPreferences
            this.ProjectFileDirty <- defaultArg projectFileDirty false

    new(parentId: int,
        assetTypeId: Nullable<int>,
        agentPreferences: string,
        parameter: string,
        projectFileId: Nullable<int>,
        ?projectFileDirty: bool,
        ?deleted: bool,
        ?storageId: Nullable<int>) =
        Asset(
            -1, parentId, assetTypeId, agentPreferences, parameter, projectFileId,
            defaultArg projectFileDirty false,
            defaultArg deleted false,
            defaultArg storageId (Nullable<int>()))

    new(src: Asset, ?assetId: int) as this =
        Asset()
        then
            let id = defaultArg assetId -1
            this.Id <- id
            this.ParentId <- src.ParentId
            this.AssetTypeId <- src.AssetTypeId
            this.Parameter <- src.Parameter
            this.Deleted <- src.Deleted
            this.ProjectFileId <- src.ProjectFileId
            this.StorageId <- src.StorageId
            this.AgentPreferences <- src.AgentPreferences
            this.ProjectFileDirty <- src.ProjectFileDirty
