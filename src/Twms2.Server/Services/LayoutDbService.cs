using Dapper;
using Twms2.Server.Data;
using Twms2.Server.Models.Twm;

namespace Twms2.Server.Services;

/// <summary>
/// 레이아웃 관련 DB 서비스.
/// TwmsLayout, TwmsBlueprintRect/Config, TwmsAssetPosition,
/// TwmsPlacementGroup, TwmsLayoutLine, TwmsLayoutGroup, Export/Import.
/// </summary>
public class LayoutDbService
{
    private readonly TwmDbConnection _db;
    private readonly ILogger<LayoutDbService> _logger;

    public LayoutDbService(TwmDbConnection db, ILogger<LayoutDbService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ──────────────── TwmsLayoutLine ────────────────

    public async Task<Dictionary<int, string>> GetTwmsLayoutLineMapAsync()
    {
        using var conn = _db.Create();
        var list = (await conn.QueryAsync<TwmsLayoutLine>("SELECT * FROM TwmsLayoutLine")).AsList();
        return list.ToDictionary(l => l.Id, l => l.Name);
    }

    public async Task UpsertTwmsLayoutLineAsync(TwmsLayoutLine line)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsLayoutLine (Id, Name, UpdatedAt)
            VALUES (@Id, @Name, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                Name      = @Name,
                UpdatedAt = CURRENT_TIMESTAMP
            """, line);
    }

    public async Task<List<TwmsLayoutLine>> GetAllTwmsLayoutLinesAsync()
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsLayoutLine>(
            "SELECT * FROM TwmsLayoutLine ORDER BY Id")).AsList();
    }

    public async Task DeleteTwmsLayoutLineAsync(int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsLayoutLine WHERE Id = @Id", new { Id = id });
    }

    public async Task DeleteAllTwmsLayoutLinesAsync()
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsLayoutLine");
    }

    public async Task<int> CountAssetsByLineIdAsync(int lineId)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TwmsAsset WHERE AugLineId = @LineId", new { LineId = lineId });
    }

    // ──────────────── TwmsLayoutGroup ────────────────

    public async Task UpsertTwmsLayoutGroupAsync(TwmsLayoutGroup group)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsLayoutGroup (Id, AssetId, Floor, Assets, UpdatedAt)
            VALUES (@Id, @AssetId, @Floor, @Assets, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                AssetId   = @AssetId,
                Floor     = @Floor,
                Assets    = @Assets,
                UpdatedAt = CURRENT_TIMESTAMP
            """, group);
    }

    public async Task<List<TwmsLayoutGroup>> GetAllTwmsLayoutGroupsAsync()
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsLayoutGroup>(
            "SELECT * FROM TwmsLayoutGroup ORDER BY Id")).AsList();
    }

    public async Task DeleteAllTwmsLayoutGroupsAsync()
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsLayoutGroup");
    }

    // ──────────────── TwmsLayout ────────────────

    public async Task<List<TwmsLayout>> GetAllLayoutsAsync()
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsLayout>(
            "SELECT * FROM TwmsLayout ORDER BY SortOrder, Id")).AsList();
    }

    public async Task<int> InsertLayoutAsync(TwmsLayout layout)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO TwmsLayout (Name, SortOrder, UpdatedAt)
            VALUES (@Name, @SortOrder, CURRENT_TIMESTAMP);
            SELECT last_insert_rowid();
            """, layout);
    }

    public async Task UpdateLayoutAsync(TwmsLayout layout)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            UPDATE TwmsLayout SET Name = @Name, SortOrder = @SortOrder,
                UpdatedAt = CURRENT_TIMESTAMP WHERE Id = @Id
            """, layout);
    }

    public async Task UpdateLayoutSortOrdersAsync(List<(int Id, int SortOrder)> sortOrders)
    {
        using var conn = _db.Create();
        using var tx = conn.BeginTransaction();
        foreach (var (id, sortOrder) in sortOrders)
        {
            await conn.ExecuteAsync(
                "UPDATE TwmsLayout SET SortOrder = @SortOrder, UpdatedAt = CURRENT_TIMESTAMP WHERE Id = @Id",
                new { Id = id, SortOrder = sortOrder }, transaction: tx);
        }
        tx.Commit();
    }

    public async Task DeleteLayoutAsync(int layoutId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsBlueprintConfig WHERE LayoutId = @Id", new { Id = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsBlueprintRect WHERE LayoutId = @Id", new { Id = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsAssetPosition WHERE LayoutId = @Id", new { Id = layoutId });
        await conn.ExecuteAsync("""
            DELETE FROM TwmsPlacementGroupMember WHERE GroupId IN
                (SELECT Id FROM TwmsPlacementGroup WHERE LayoutId = @Id)
            """, new { Id = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroup WHERE LayoutId = @Id", new { Id = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsLayout WHERE Id = @Id", new { Id = layoutId });
    }

    public async Task<int> DuplicateLayoutAsync(int sourceLayoutId, string newName)
    {
        using var conn = _db.Create();
        using var tx = conn.BeginTransaction();
        try
        {
            var newId = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO TwmsLayout (Name, SortOrder, UpdatedAt)
                VALUES (@Name, (SELECT COALESCE(MAX(SortOrder),0)+1 FROM TwmsLayout), CURRENT_TIMESTAMP);
                SELECT last_insert_rowid();
                """, new { Name = newName }, transaction: tx);

            await conn.ExecuteAsync("""
                INSERT INTO TwmsBlueprintConfig (LayoutId, ImagePath, ImageWidth, ImageHeight, DrawingData, BgColor, GridColor, UpdatedAt)
                SELECT @NewId, ImagePath, ImageWidth, ImageHeight, DrawingData, BgColor, GridColor, CURRENT_TIMESTAMP
                FROM TwmsBlueprintConfig WHERE LayoutId = @SrcId
                """, new { NewId = newId, SrcId = sourceLayoutId }, transaction: tx);

            await conn.ExecuteAsync("""
                INSERT INTO TwmsBlueprintRect (LayoutId, LineId, X, Y, Width, Height, UpdatedAt)
                SELECT @NewId, LineId, X, Y, Width, Height, CURRENT_TIMESTAMP
                FROM TwmsBlueprintRect WHERE LayoutId = @SrcId
                """, new { NewId = newId, SrcId = sourceLayoutId }, transaction: tx);

            await conn.ExecuteAsync("""
                INSERT INTO TwmsAssetPosition (LayoutId, AssetId, X, Y, Scale, Visible, UpdatedAt)
                SELECT @NewId, AssetId, X, Y, Scale, Visible, CURRENT_TIMESTAMP
                FROM TwmsAssetPosition WHERE LayoutId = @SrcId
                """, new { NewId = newId, SrcId = sourceLayoutId }, transaction: tx);

            var srcGroups = (await conn.QueryAsync<TwmsPlacementGroup>(
                "SELECT * FROM TwmsPlacementGroup WHERE LayoutId = @SrcId",
                new { SrcId = sourceLayoutId }, transaction: tx)).AsList();
            foreach (var g in srcGroups)
            {
                var newGid = await conn.ExecuteScalarAsync<int>("""
                    INSERT INTO TwmsPlacementGroup (LayoutId, Name, X, Y, Width, Height, Color, UpdatedAt)
                    VALUES (@LayoutId, @Name, @X, @Y, @Width, @Height, @Color, CURRENT_TIMESTAMP);
                    SELECT last_insert_rowid();
                    """, new { LayoutId = newId, g.Name, g.X, g.Y, g.Width, g.Height, g.Color }, transaction: tx);
                await conn.ExecuteAsync("""
                    INSERT INTO TwmsPlacementGroupMember (GroupId, AssetId)
                    SELECT @NewGid, AssetId FROM TwmsPlacementGroupMember WHERE GroupId = @OldGid
                    """, new { NewGid = newGid, OldGid = g.Id }, transaction: tx);
            }

            tx.Commit();
            return newId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ──────────────── TwmsBlueprintRect / Config ────────────────

    public async Task<List<TwmsBlueprintRect>> GetAllBlueprintRectsAsync(int layoutId)
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsBlueprintRect>(
            "SELECT * FROM TwmsBlueprintRect WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId })).AsList();
    }

    public async Task UpsertBlueprintRectAsync(TwmsBlueprintRect rect)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsBlueprintRect (LayoutId, LineId, X, Y, Width, Height, UpdatedAt)
            VALUES (@LayoutId, @LineId, @X, @Y, @Width, @Height, CURRENT_TIMESTAMP)
            ON CONFLICT(LayoutId, LineId) DO UPDATE SET
                X = @X, Y = @Y, Width = @Width, Height = @Height,
                UpdatedAt = CURRENT_TIMESTAMP
            """, rect);
    }

    public async Task DeleteBlueprintRectAsync(int layoutId, int lineId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "DELETE FROM TwmsBlueprintRect WHERE LayoutId = @LayoutId AND LineId = @LineId",
            new { LayoutId = layoutId, LineId = lineId });
    }

    public async Task<TwmsBlueprintConfig?> GetBlueprintConfigAsync(int layoutId)
    {
        using var conn = _db.Create();
        return await conn.QueryFirstOrDefaultAsync<TwmsBlueprintConfig>(
            "SELECT * FROM TwmsBlueprintConfig WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId });
    }

    public async Task UpsertBlueprintConfigAsync(TwmsBlueprintConfig config)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsBlueprintConfig (LayoutId, ImagePath, ImageWidth, ImageHeight, DrawingData, BgColor, GridColor, GridEnabled, GridSize, UpdatedAt)
            VALUES (@LayoutId, @ImagePath, @ImageWidth, @ImageHeight, @DrawingData, @BgColor, @GridColor, @GridEnabled, @GridSize, CURRENT_TIMESTAMP)
            ON CONFLICT(LayoutId) DO UPDATE SET
                ImagePath   = @ImagePath,
                ImageWidth  = @ImageWidth,
                ImageHeight = @ImageHeight,
                DrawingData = @DrawingData,
                BgColor     = @BgColor,
                GridColor   = @GridColor,
                GridEnabled = @GridEnabled,
                GridSize    = @GridSize,
                UpdatedAt   = CURRENT_TIMESTAMP
            """, config);
    }

    // ──────────────── TwmsAssetPosition ────────────────

    public async Task<List<TwmsAssetPosition>> GetAllAssetPositionsAsync(int layoutId)
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsAssetPosition>(
            "SELECT * FROM TwmsAssetPosition WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId })).AsList();
    }

    public async Task<HashSet<int>> GetAssetIdsPlacedOnOtherLayoutsAsync(int layoutId)
    {
        using var conn = _db.Create();
        var ids = await conn.QueryAsync<int>("""
            SELECT DISTINCT AssetId FROM TwmsAssetPosition
            WHERE LayoutId <> @LayoutId AND Visible = 1
            UNION
            SELECT DISTINCT m.AssetId FROM TwmsPlacementGroupMember m
            JOIN TwmsPlacementGroup g ON g.Id = m.GroupId
            WHERE g.LayoutId <> @LayoutId
            """, new { LayoutId = layoutId });
        return new HashSet<int>(ids);
    }

    public async Task UpsertAssetPositionAsync(TwmsAssetPosition pos)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsAssetPosition (LayoutId, AssetId, X, Y, Scale, Visible, UpdatedAt)
            VALUES (@LayoutId, @AssetId, @X, @Y, @Scale, @Visible, CURRENT_TIMESTAMP)
            ON CONFLICT(LayoutId, AssetId) DO UPDATE SET
                X = @X, Y = @Y, Scale = @Scale, Visible = @Visible,
                UpdatedAt = CURRENT_TIMESTAMP
            """, pos);
    }

    public async Task UpsertAssetPositionBatchAsync(List<TwmsAssetPosition> positions)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsAssetPosition (LayoutId, AssetId, X, Y, Scale, Visible, UpdatedAt)
            VALUES (@LayoutId, @AssetId, @X, @Y, @Scale, @Visible, CURRENT_TIMESTAMP)
            ON CONFLICT(LayoutId, AssetId) DO UPDATE SET
                X = @X, Y = @Y, Scale = @Scale, Visible = @Visible,
                UpdatedAt = CURRENT_TIMESTAMP
            """, positions);
    }

    // ──────────────── TwmsPlacementGroup ────────────────

    public async Task<List<TwmsPlacementGroup>> GetAllPlacementGroupsAsync(int layoutId)
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsPlacementGroup>(
            "SELECT * FROM TwmsPlacementGroup WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId })).AsList();
    }

    public async Task<List<TwmsPlacementGroupMember>> GetPlacementGroupMembersAsync(int layoutId)
    {
        using var conn = _db.Create();
        return (await conn.QueryAsync<TwmsPlacementGroupMember>("""
            SELECT m.GroupId, m.AssetId
            FROM TwmsPlacementGroupMember m
            JOIN TwmsPlacementGroup g ON g.Id = m.GroupId
            WHERE g.LayoutId = @LayoutId
            """, new { LayoutId = layoutId })).AsList();
    }

    public async Task<int> InsertPlacementGroupAsync(TwmsPlacementGroup group)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO TwmsPlacementGroup (LayoutId, Name, X, Y, Width, Height, Color, Floor, UpdatedAt)
            VALUES (@LayoutId, @Name, @X, @Y, @Width, @Height, @Color, @Floor, CURRENT_TIMESTAMP);
            SELECT last_insert_rowid();
            """, group);
    }

    public async Task UpsertPlacementGroupAsync(TwmsPlacementGroup group)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsPlacementGroup (Id, LayoutId, Name, X, Y, Width, Height, Color, Floor, UpdatedAt)
            VALUES (@Id, @LayoutId, @Name, @X, @Y, @Width, @Height, @Color, @Floor, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name, X = excluded.X, Y = excluded.Y,
                Width = excluded.Width, Height = excluded.Height,
                Color = excluded.Color, Floor = excluded.Floor, UpdatedAt = CURRENT_TIMESTAMP
            """, group);
    }

    public async Task DeletePlacementGroupAsync(int groupId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroupMember WHERE GroupId = @Id", new { Id = groupId });
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroup WHERE Id = @Id", new { Id = groupId });
    }

    public async Task SetPlacementGroupMembersAsync(int groupId, List<int> assetIds)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroupMember WHERE GroupId = @GroupId",
            new { GroupId = groupId });
        if (assetIds.Count > 0)
            await conn.ExecuteAsync(
                "INSERT INTO TwmsPlacementGroupMember (GroupId, AssetId) VALUES (@GroupId, @AssetId)",
                assetIds.Select(a => new { GroupId = groupId, AssetId = a }));
    }

    public async Task UpsertPlacementGroupBatchAsync(List<TwmsPlacementGroup> groups)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            INSERT INTO TwmsPlacementGroup (Id, LayoutId, Name, X, Y, Width, Height, Color, Floor, UpdatedAt)
            VALUES (@Id, @LayoutId, @Name, @X, @Y, @Width, @Height, @Color, @Floor, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name, X = excluded.X, Y = excluded.Y,
                Width = excluded.Width, Height = excluded.Height,
                Color = excluded.Color, Floor = excluded.Floor, UpdatedAt = CURRENT_TIMESTAMP
            """, groups);
    }

    public async Task DeleteAllPlacementGroupsAsync(int layoutId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("""
            DELETE FROM TwmsPlacementGroupMember WHERE GroupId IN
                (SELECT Id FROM TwmsPlacementGroup WHERE LayoutId = @LayoutId)
            """, new { LayoutId = layoutId });
        await conn.ExecuteAsync("DELETE FROM TwmsPlacementGroup WHERE LayoutId = @LayoutId",
            new { LayoutId = layoutId });
    }

    // ──────────────── 레이아웃 Export / Import ────────────────

    public async Task<LayoutExportData> ExportLayoutAsync(int layoutId, string? layoutName)
    {
        var config = await GetBlueprintConfigAsync(layoutId);
        var rects = await GetAllBlueprintRectsAsync(layoutId);
        var positions = await GetAllAssetPositionsAsync(layoutId);
        var groups = await GetAllPlacementGroupsAsync(layoutId);
        var members = await GetPlacementGroupMembersAsync(layoutId);
        var memberMap = members.GroupBy(m => m.GroupId).ToDictionary(g => g.Key, g => g.Select(m => m.AssetId).ToList());

        return new LayoutExportData
        {
            Version = 1,
            LayoutName = layoutName,
            ExportedAt = DateTime.Now,
            Config = config != null ? new LayoutExportConfig
            {
                BgColor = config.BgColor,
                GridColor = config.GridColor,
                ImageWidth = config.ImageWidth,
                ImageHeight = config.ImageHeight,
            } : null,
            BlueprintRects = rects.Select(r => new LayoutExportRect
            {
                LineId = r.LineId, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
            }).ToList(),
            Positions = positions.Where(p => p.Visible).Select(p => new LayoutExportPosition
            {
                AssetId = p.AssetId, X = p.X, Y = p.Y, Scale = p.Scale, Visible = p.Visible,
            }).ToList(),
            Groups = groups.Select(g => new LayoutExportGroup
            {
                Name = g.Name, X = g.X, Y = g.Y, Width = g.Width, Height = g.Height, Color = g.Color,
                Floor = g.Floor,
                MemberAssetIds = memberMap.GetValueOrDefault(g.Id) ?? [],
            }).ToList(),
        };
    }

    public async Task<(int positions, int groups, int rects, int skipped)> ImportLayoutAsync(
        int layoutId, LayoutExportData data, HashSet<int> validAssetIds)
    {
        using var conn = _db.Create();
        int skipped = 0;

        await conn.ExecuteAsync("DELETE FROM TwmsBlueprintRect WHERE LayoutId = @Id", new { Id = layoutId });
        foreach (var r in data.BlueprintRects)
        {
            await conn.ExecuteAsync("""
                INSERT INTO TwmsBlueprintRect (LayoutId, LineId, X, Y, Width, Height, UpdatedAt)
                VALUES (@LayoutId, @LineId, @X, @Y, @Width, @Height, CURRENT_TIMESTAMP)
                """, new { LayoutId = layoutId, r.LineId, r.X, r.Y, r.Width, r.Height });
        }

        if (data.Config != null)
        {
            var existing = await GetBlueprintConfigAsync(layoutId);
            if (existing != null)
            {
                existing.BgColor = data.Config.BgColor;
                existing.GridColor = data.Config.GridColor;
                existing.ImageWidth = data.Config.ImageWidth;
                existing.ImageHeight = data.Config.ImageHeight;
                await UpsertBlueprintConfigAsync(existing);
            }
        }

        await conn.ExecuteAsync("DELETE FROM TwmsAssetPosition WHERE LayoutId = @Id", new { Id = layoutId });
        var validPositions = new List<TwmsAssetPosition>();
        foreach (var p in data.Positions)
        {
            if (!validAssetIds.Contains(p.AssetId)) { skipped++; continue; }
            validPositions.Add(new TwmsAssetPosition
            {
                LayoutId = layoutId, AssetId = p.AssetId,
                X = p.X, Y = p.Y, Scale = p.Scale, Visible = p.Visible,
            });
        }
        if (validPositions.Count > 0)
            await UpsertAssetPositionBatchAsync(validPositions);

        await DeleteAllPlacementGroupsAsync(layoutId);
        int groupCount = 0;
        foreach (var g in data.Groups)
        {
            var validMembers = g.MemberAssetIds.Where(validAssetIds.Contains).ToList();
            if (validMembers.Count == 0) { skipped++; continue; }

            var newGroup = new TwmsPlacementGroup
            {
                LayoutId = layoutId, Name = g.Name,
                X = g.X, Y = g.Y, Width = g.Width, Height = g.Height, Color = g.Color,
                Floor = g.Floor,
            };
            var newId = await InsertPlacementGroupAsync(newGroup);
            await SetPlacementGroupMembersAsync(newId, validMembers);
            groupCount++;
        }

        return (validPositions.Count, groupCount, data.BlueprintRects.Count, skipped);
    }
}
