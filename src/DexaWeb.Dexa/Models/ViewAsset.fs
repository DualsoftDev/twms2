namespace DEX.Core.Database.ORM

type ViewAsset() =
    member val Asset: Asset = Asset() with get, set
    member val AssetType: AssetType = AssetType() with get, set
    member val ProjectFile: ProjectFile = ProjectFile() with get, set

    member this.AssetId
        with get () = this.Asset.Id
        and set (v) = this.Asset.Id <- v

    member this.AssetName = this.Asset.Name

    member this.AssetParentId
        with get () = this.Asset.ParentId
        and set (v) = this.Asset.ParentId <- v

    member this.AssetStorageId
        with get () = this.Asset.StorageId
        and set (v) = this.Asset.StorageId <- v

    member this.AssetAgentPreferences
        with get () = this.Asset.AgentPreferences
        and set (v) = this.Asset.AgentPreferences <- v

    member this.AssetParameter
        with get () = this.Asset.Parameter
        and set (v) = this.Asset.Parameter <- v

    member this.AssetProjectFileId
        with get () = this.Asset.ProjectFileId
        and set (v) = this.Asset.ProjectFileId <- v

    member this.AssetProjectFileDirty
        with get () = this.Asset.ProjectFileDirty
        and set (v) = this.Asset.ProjectFileDirty <- v

    member this.AssetDeleted
        with get () = this.Asset.Deleted
        and set (v) = this.Asset.Deleted <- v

    member this.AssetDescription = this.Asset.Description

    member this.AssetTypeId
        with get () = this.Asset.AssetTypeId
        and set (v) =
            this.AssetType.Id <- v
            this.Asset.AssetTypeId <- v

    member this.AssetTypeUserFriendlyName
        with get () = this.AssetType.UserFriendlyName
        and set (v) = this.AssetType.UserFriendlyName <- v

    member this.AssetTypeGuid
        with get () = this.AssetType.Guid
        and set (v) = this.AssetType.Guid <- v

    member this.AssetTypeFake
        with get () = this.AssetType.Fake
        and set (v) = this.AssetType.Fake <- v

    member this.AssetTypeIcon
        with get () = this.AssetType.Icon
        and set (v) = this.AssetType.Icon <- v

    member this.AssetTypeParameter
        with get () = this.AssetType.Parameter
        and set (v) = this.AssetType.Parameter <- v

    member val AssetTypeDotnetClassName: string = null with get, set

    member this.ProjectFilePath
        with get () = this.ProjectFile.Path
        and set (v) = this.ProjectFile.Path <- v

    member this.ProjectFileChecksum
        with get () = this.ProjectFile.Checksum
        and set (v) = this.ProjectFile.Checksum <- v

    member this.ProjectFileContents
        with get () = this.ProjectFile.FileContents
        and set (v) = this.ProjectFile.FileContents <- v

    member this.ProjectFileModifiedByClient
        with get () = this.ProjectFile.ModifiedByClient
        and set (v) = this.ProjectFile.ModifiedByClient <- v

    new(asset: Asset, assetType: AssetType, projectFile: ProjectFile) as this =
        ViewAsset()
        then
            this.Asset <- asset
            this.AssetType <- assetType
            this.ProjectFile <- projectFile

    member this.IsSystemRootFolderAsset =
        this.Asset.AssetTypeId.HasValue && (if this.Asset.AssetTypeId.HasValue then this.Asset.AssetTypeId.Value else 0) = 1

    member this.IsNonRootFolderAsset =
        this.Asset.AssetTypeId.HasValue && (if this.Asset.AssetTypeId.HasValue then this.Asset.AssetTypeId.Value else 0) = 2

    member this.IsFolderAsset =
        this.Asset.IsSystemRootFolderAsset || this.Asset.IsNonRootFolderAsset

    member this.IsRealAsset = not this.Asset.IsFolderAsset
