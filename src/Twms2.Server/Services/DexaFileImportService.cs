using Dapper;
using Twms2.Server.Models.Twm;
using Microsoft.Data.Sqlite;

namespace Twms2.Server.Services;

/// <summary>
/// 사용자가 업로드한 DEXA.sqlite3 파일에서 데이터를 읽어 TWM DB에 가져오기.
/// - asset: augStationNumber, augVendor, augSpec, augLineId (전 타입)
///          augIp, augIpVia, augBaseNumber, augSlotNumber, augIsRobotPLC (PLC/Servo)
/// - layoutLine: id, name
/// - layoutGroup: id, assetId, floor, assets
/// </summary>
public class DexaFileImportService(TwmDbService twmDb, ILogger<DexaFileImportService> logger)
{
    private const int AssetTypeXgtPlc = 6;
    private const int AssetTypeServo  = 7;

    public async Task<DexaImportResult> ImportFromFileAsync(string sqliteFilePath)
    {
        var result = new DexaImportResult();

        try
        {
            var connStr = $"Data Source={sqliteFilePath};Mode=ReadOnly;";

            List<DexaAssetRow>  assetRows;
            List<DexaLineRow>   lineRows;
            List<DexaGroupRow>  groupRows;

            using (var conn = new SqliteConnection(connStr))
            {
                await conn.OpenAsync();

                assetRows = (await conn.QueryAsync<DexaAssetRow>("""
                    SELECT id, assetTypeId,
                           augStationNumber, augVendor, augSpec, augLineId,
                           augIp, augIpVia, augBaseNumber, augSlotNumber, augIsRobotPLC
                    FROM asset
                    WHERE deleted = 0
                      AND assetTypeId BETWEEN 3 AND 99
                    """)).ToList();

                // layoutLine 테이블이 없는 이전 DEXA 버전 대비 방어
                lineRows = await SafeQueryAsync<DexaLineRow>(conn,
                    "SELECT id, name FROM layoutLine");

                groupRows = await SafeQueryAsync<DexaGroupRow>(conn,
                    "SELECT id, assetId, floor, assets FROM layoutGroup");
            }
            // 연결 풀에서 완전히 해제 (파일 삭제 전 필수)
            SqliteConnection.ClearAllPools();

            result.TotalRead = assetRows.Count;

            // ── asset aug 데이터 ──
            foreach (var row in assetRows)
            {
                try { await ImportAssetRowAsync(row, result); }
                catch (Exception ex)
                {
                    result.ErrorCount++;
                    logger.LogWarning(ex, "자산 {Id} 가져오기 실패", row.Id);
                }
            }

            // ── layoutLine (전체 교체) ──
            if (lineRows.Count > 0)
            {
                await twmDb.DeleteAllTwmsLayoutLinesAsync();
                foreach (var line in lineRows)
                {
                    await twmDb.UpsertTwmsLayoutLineAsync(new TwmsLayoutLine
                    {
                        Id   = line.Id,
                        Name = line.Name ?? $"Line#{line.Id}",
                    });
                }
                result.LineImported = lineRows.Count;
            }

            // ── layoutGroup (전체 교체) ──
            if (groupRows.Count > 0)
            {
                await twmDb.DeleteAllTwmsLayoutGroupsAsync();
                foreach (var grp in groupRows)
                {
                    await twmDb.UpsertTwmsLayoutGroupAsync(new TwmsLayoutGroup
                    {
                        Id      = grp.Id,
                        AssetId = grp.AssetId,
                        Floor   = grp.Floor,
                        Assets  = grp.Assets,
                    });
                }
                result.GroupImported = groupRows.Count;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DEXA 파일 가져오기 실패: {Path}", sqliteFilePath);
            result.FatalError = ex.Message;
        }

        return result;
    }

    private async Task ImportAssetRowAsync(DexaAssetRow row, DexaImportResult result)
    {
        // ── TwmsAsset (전 타입) ──
        if (row.AugStationNumber.HasValue || row.AugVendor != null
            || row.AugSpec != null || row.AugLineId.HasValue)
        {
            await twmDb.UpsertTwmsAssetAsync(new TwmsAsset
            {
                DexaId           = row.Id,
                AugStationNumber = row.AugStationNumber,
                AugVendor        = row.AugVendor,
                AugSpec          = row.AugSpec,
                AugLineId        = row.AugLineId,
            });
            result.AugImported++;
        }

        // ── TwmsAssetConn (PLC/Servo 전용) ──
        bool isPlcOrServo = row.AssetTypeId == AssetTypeXgtPlc || row.AssetTypeId == AssetTypeServo;
        if (!isPlcOrServo) return;

        if (string.IsNullOrWhiteSpace(row.AugIp))
        {
            result.ConnSkipped++;
            return;
        }

        await twmDb.UpsertTwmsAssetConnAsync(new TwmsAssetConn
        {
            DexaId        = row.Id,
            AugIp         = row.AugIp,
            AugIpVia      = row.AugIpVia,
            AugBaseNumber = row.AugBaseNumber ?? 0,
            AugSlotNumber = row.AugSlotNumber,
            AugIsRobotPLC = row.AugIsRobotPLC,
        });
        result.ConnImported++;
    }

    // ──────────────── 좌표 임포트 ────────────────

    /// <summary>
    /// DEXA.sqlite3에서 라인 목록을 미리 읽어 UI에서 매핑 테이블 표시용으로 반환.
    /// </summary>
    public async Task<List<DexaLinePreview>> PreviewLinesFromFileAsync(string sqliteFilePath)
    {
        var connStr = $"Data Source={sqliteFilePath};Mode=ReadOnly;";
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        var rows = await SafeQueryAsync<DexaLayoutLineFullRow>(conn,
            "SELECT id, name, x, y, w, h, selfW, selfH FROM layoutLine");
        SqliteConnection.ClearAllPools();
        return rows.Select(r => new DexaLinePreview
        {
            Id = r.Id, Name = r.Name, SelfW = r.SelfW, SelfH = r.SelfH,
        }).ToList();
    }

    /// <summary>
    /// DEXA.sqlite3에서 자산 배치 좌표, 그룹 정보를 읽어
    /// 라인↔레이아웃 매핑에 따라 각 도면에 배치.
    /// </summary>
    public async Task<DexaPositionImportResult> ImportPositionsFromFileAsync(
        string sqliteFilePath, Dictionary<int, int> lineLayoutMap)
    {
        var result = new DexaPositionImportResult();
        const double ICON_HALF = 16;
        const double VB_W = 1000, VB_H = 600;
        const double ICON_SIZE = 32;

        try
        {
            var connStr = $"Data Source={sqliteFilePath};Mode=ReadOnly;";

            List<DexaLayoutLineFullRow> lineRows;
            List<DexaAssetPosRow> assetPosRows;
            List<DexaGroupFullRow> groupRows;
            Dictionary<int, int> allAssetLineMap; // 모든 자산의 lineId (그룹 역추적용)

            using (var conn = new SqliteConnection(connStr))
            {
                await conn.OpenAsync();

                // 라인 (좌표 포함)
                lineRows = await SafeQueryAsync<DexaLayoutLineFullRow>(conn,
                    "SELECT id, name, x, y, w, h, selfW, selfH FROM layoutLine");

                // 자산 좌표 (중심점) — 배치 대상
                assetPosRows = await SafeQueryAsync<DexaAssetPosRow>(conn, """
                    SELECT id, augLX, augLY, augLineId
                    FROM asset
                    WHERE deleted = 0 AND augLX IS NOT NULL AND augLY IS NOT NULL
                    """);

                // 모든 자산의 lineId (좌표 유무 무관 — 그룹 역추적용)
                var allAssets = await SafeQueryAsync<DexaAssetPosRow>(conn,
                    "SELECT id, 0 AS augLX, 0 AS augLY, augLineId FROM asset WHERE deleted = 0 AND augLineId IS NOT NULL");
                allAssetLineMap = allAssets.ToDictionary(a => a.Id, a => a.AugLineId!.Value);

                // 그룹 (assetId는 소속 라인 역추적용)
                groupRows = await SafeQueryAsync<DexaGroupFullRow>(conn,
                    "SELECT assetId, floor, x, y, w, h, assets FROM layoutGroup");
            }
            SqliteConnection.ClearAllPools();

            // 매핑된 라인만 처리, lineId → (layoutId, scaleX, scaleY)
            // 라인 영역(BlueprintRect)은 건드리지 않음 — 별도 관리 기능
            var lineMap = new Dictionary<int, (int layoutId, double scaleX, double scaleY)>();

            foreach (var line in lineRows)
            {
                if (!lineLayoutMap.TryGetValue(line.Id, out var layoutId))
                {
                    result.Skipped++;
                    continue;
                }
                if (line.SelfW <= 0 || line.SelfH <= 0) { result.Skipped++; continue; }

                var scaleX = VB_W / line.SelfW;
                var scaleY = VB_H / line.SelfH;
                lineMap[line.Id] = (layoutId, scaleX, scaleY);
            }

            // assetPosRows 전용 lineId lookup (자산 배치용)
            var assetLineMap = assetPosRows
                .Where(a => a.AugLineId.HasValue)
                .ToDictionary(a => a.Id, a => a.AugLineId!.Value);

            // ── 자산 위치 → TwmsAssetPosition (라인별 도면에 배치) ──
            var assetPositions = new List<TwmsAssetPosition>();
            foreach (var asset in assetPosRows)
            {
                if (!asset.AugLineId.HasValue || !lineMap.TryGetValue(asset.AugLineId.Value, out var lr))
                {
                    result.Skipped++;
                    continue;
                }

                var cx = asset.AugLX * lr.scaleX;
                var cy = asset.AugLY * lr.scaleY;
                var px = Math.Clamp(cx - ICON_HALF, 0, VB_W - ICON_SIZE);
                var py = Math.Clamp(cy - ICON_HALF, 0, VB_H - ICON_SIZE);

                assetPositions.Add(new TwmsAssetPosition
                {
                    LayoutId = lr.layoutId,
                    AssetId  = asset.Id,
                    X        = Math.Round(px, 2),
                    Y        = Math.Round(py, 2),
                    Scale    = 1.0,
                    Visible  = true,
                });
            }
            if (assetPositions.Count > 0)
                await twmDb.UpsertAssetPositionBatchAsync(assetPositions);
            result.AssetPositionsImported = assetPositions.Count;

            // ── 그룹 → TwmsPlacementGroup + 멤버 ──
            // 매핑된 레이아웃들의 기존 그룹 삭제
            foreach (var layoutId in lineLayoutMap.Values.Distinct())
                await twmDb.DeleteAllPlacementGroupsAsync(layoutId);

            foreach (var grp in groupRows)
            {
                // assetId(PLC)의 augLineId로 소속 라인 역추적 (좌표 유무 무관)
                if (!allAssetLineMap.TryGetValue(grp.AssetId, out var lineId)
                    || !lineMap.TryGetValue(lineId, out var glr))
                {
                    result.Skipped++;
                    continue;
                }

                var gx = grp.X * glr.scaleX;
                var gy = grp.Y * glr.scaleY;
                var gw = grp.W * glr.scaleX;
                var gh = grp.H * glr.scaleY;

                var floorLabel = grp.Floor.HasValue ? $"{grp.Floor}층" : "";
                var newGroup = new TwmsPlacementGroup
                {
                    LayoutId = glr.layoutId,
                    Name     = floorLabel,
                    X        = Math.Round(Math.Clamp(gx, 0, VB_W), 2),
                    Y        = Math.Round(Math.Clamp(gy, 0, VB_H), 2),
                    Width    = Math.Round(Math.Max(gw, 10), 2),
                    Height   = Math.Round(Math.Max(gh, 10), 2),
                };
                var newGroupId = await twmDb.InsertPlacementGroupAsync(newGroup);

                // 멤버 파싱
                if (!string.IsNullOrWhiteSpace(grp.Assets))
                {
                    var memberIds = grp.Assets.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
                        .Where(v => v > 0)
                        .ToList();
                    if (memberIds.Count > 0)
                        await twmDb.SetPlacementGroupMembersAsync(newGroupId, memberIds);
                }
                result.GroupsImported++;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DEXA 좌표 가져오기 실패: {Path}", sqliteFilePath);
            result.FatalError = ex.Message;
        }

        return result;
    }

    // ──────────────── 공통 ────────────────

    /// <summary>테이블이 없는 경우 빈 목록을 반환하는 방어적 쿼리</summary>
    private static async Task<List<T>> SafeQueryAsync<T>(SqliteConnection conn, string sql)
    {
        try { return (await conn.QueryAsync<T>(sql)).ToList(); }
        catch { return []; }
    }

    // ── 내부 DTO ──

    private class DexaAssetRow
    {
        public int     Id               { get; set; }
        public int     AssetTypeId      { get; set; }
        public int?    AugStationNumber { get; set; }
        public string? AugVendor        { get; set; }
        public string? AugSpec          { get; set; }
        public int?    AugLineId        { get; set; }
        public string? AugIp            { get; set; }
        public string? AugIpVia         { get; set; }
        public int?    AugBaseNumber    { get; set; }
        public int?    AugSlotNumber    { get; set; }
        public int?    AugIsRobotPLC    { get; set; }
    }

    private class DexaLineRow
    {
        public int     Id   { get; set; }
        public string? Name { get; set; }
    }

    private class DexaGroupRow
    {
        public int     Id      { get; set; }
        public int     AssetId { get; set; }
        public int?    Floor   { get; set; }
        public string? Assets  { get; set; }
    }

    // ── 좌표 임포트용 DTO ──

    private class DexaLayoutLineFullRow
    {
        public int    Id    { get; set; }
        public string Name  { get; set; } = "";
        public double X     { get; set; }
        public double Y     { get; set; }
        public double W     { get; set; }
        public double H     { get; set; }
        public double SelfW { get; set; }
        public double SelfH { get; set; }
    }

    private class DexaAssetPosRow
    {
        public int     Id        { get; set; }
        public double  AugLX     { get; set; }
        public double  AugLY     { get; set; }
        public int?    AugLineId { get; set; }
    }

    private class DexaGroupFullRow
    {
        public int     AssetId { get; set; }
        public int?    Floor   { get; set; }
        public double  X       { get; set; }
        public double  Y       { get; set; }
        public double  W       { get; set; }
        public double  H       { get; set; }
        public string? Assets  { get; set; }
    }
}

public class DexaImportResult
{
    public int     TotalRead    { get; set; }
    public int     AugImported  { get; set; }
    public int     ConnImported { get; set; }
    public int     ConnSkipped  { get; set; }
    public int     LineImported { get; set; }
    public int     GroupImported { get; set; }
    public int     ErrorCount   { get; set; }
    public string? FatalError   { get; set; }

    public bool HasFatalError => FatalError != null;
}

public class DexaLinePreview
{
    public int    Id    { get; set; }
    public string Name  { get; set; } = "";
    public double SelfW { get; set; }
    public double SelfH { get; set; }
}

public class DexaPositionImportResult
{
    public int     AssetPositionsImported { get; set; }
    public int     GroupsImported         { get; set; }
    public int     Skipped                { get; set; }
    public string? FatalError             { get; set; }

    public bool HasFatalError => FatalError != null;
}
