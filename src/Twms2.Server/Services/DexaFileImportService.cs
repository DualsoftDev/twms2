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
public class DexaFileImportService(TwmDbService twmDb, LayoutDbService layoutDb, IWebHostEnvironment webEnv, ILogger<DexaFileImportService> logger)
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
                await layoutDb.DeleteAllTwmsLayoutLinesAsync();
                foreach (var line in lineRows)
                {
                    await layoutDb.UpsertTwmsLayoutLineAsync(new TwmsLayoutLine
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
                await layoutDb.DeleteAllTwmsLayoutGroupsAsync();
                foreach (var grp in groupRows)
                {
                    await layoutDb.UpsertTwmsLayoutGroupAsync(new TwmsLayoutGroup
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
        string sqliteFilePath, Dictionary<int, int> lineLayoutMap, int? refLineId = null)
    {
        var result = new DexaPositionImportResult();
        const double VB_W = 1000, VB_H = 600;

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

            // ── TWM1 좌표 변환 ──
            // TWM1에서 모든 자산/그룹의 augLX/augLY 좌표는 "도면 라인"(배경 이미지 라인)의
            // selfW x selfH 좌표 공간에 기록됨 (자산이 속한 라인의 selfW가 아님).
            // 도면 라인 = 이미지 비율과 일치하는 라인.

            // 매핑된 라인 → layoutId
            var lineToLayoutId = new Dictionary<int, int>();
            foreach (var line in lineRows)
            {
                if (lineLayoutMap.TryGetValue(line.Id, out var layoutId))
                    lineToLayoutId[line.Id] = layoutId;
            }

            // 레이아웃별 변환 파라미터: 도면 라인의 selfW/selfH + 이미지 영역
            var layoutTransform = new Dictionary<int, (double refW, double refH,
                                                       double imgAreaW, double imgAreaH,
                                                       double offsetX, double offsetY)>();

            foreach (var layoutId in lineToLayoutId.Values.Distinct())
            {
                var mappedLines = lineRows.Where(l => lineToLayoutId.GetValueOrDefault(l.Id) == layoutId).ToList();
                if (mappedLines.Count == 0) continue;

                // 기준 도면 라인의 selfW/selfH = 좌표 공간
                var config = await layoutDb.GetBlueprintConfigAsync(layoutId);
                double refW, refH;

                // 1) 명시적으로 지정된 기준 라인 사용
                var refLine = refLineId.HasValue
                    ? lineRows.FirstOrDefault(l => l.Id == refLineId.Value)
                    : null;

                if (refLine != null)
                {
                    refW = refLine.SelfW;
                    refH = refLine.SelfH;
                }
                else
                {
                    // 2) fallback: 이미지 파일 크기로 자동 감지
                    var (imgW, imgH) = await GetActualImageSizeAsync(layoutId);
                    if (imgW > 0 && imgH > 0)
                    {
                        refW = imgW;
                        refH = imgH;
                    }
                    else
                    {
                        var bgLine = lineRows.OrderBy(l => l.SelfW).First();
                        refW = bgLine.SelfW;
                        refH = bgLine.SelfH;
                    }
                }

                // config에 기준 라인의 selfW/selfH 저장 → CalcImageRect와 동일한 offset
                if (config != null)
                {
                    config.ImageWidth = refW;
                    config.ImageHeight = refH;
                    await layoutDb.UpsertBlueprintConfigAsync(config);
                }

                // viewBox 내 이미지 영역 — CalcImageRect와 동일 로직
                var ratio = refW / refH;
                var vbRatio = VB_W / VB_H;
                double imgAreaW, imgAreaH, offsetX, offsetY;

                if (ratio > vbRatio)
                {
                    imgAreaW = VB_W;
                    imgAreaH = VB_W / ratio;
                    offsetX  = 0;
                    offsetY  = (VB_H - imgAreaH) / 2;
                }
                else
                {
                    imgAreaH = VB_H;
                    imgAreaW = VB_H * ratio;
                    offsetX  = (VB_W - imgAreaW) / 2;
                    offsetY  = 0;
                }

                layoutTransform[layoutId] = (refW, refH, imgAreaW, imgAreaH, offsetX, offsetY);
            }

            // assetPosRows 전용 lineId lookup (자산 배치용)
            var assetLineMap = assetPosRows
                .Where(a => a.AugLineId.HasValue)
                .ToDictionary(a => a.Id, a => a.AugLineId!.Value);

            // ── 자산 위치 → TwmsAssetPosition (도면에 배치) ──
            var assetPositions = new List<TwmsAssetPosition>();
            foreach (var asset in assetPosRows)
            {
                if (!asset.AugLineId.HasValue
                    || !lineToLayoutId.TryGetValue(asset.AugLineId.Value, out var layoutId)
                    || !layoutTransform.TryGetValue(layoutId, out var lt))
                {
                    result.Skipped++;
                    continue;
                }

                // augLX/augLY는 도면 라인의 selfW x selfH 좌표 → 정규화 → viewBox 매핑
                var cx = (asset.AugLX / lt.refW) * lt.imgAreaW + lt.offsetX;
                var cy = (asset.AugLY / lt.refH) * lt.imgAreaH + lt.offsetY;

                assetPositions.Add(new TwmsAssetPosition
                {
                    LayoutId = layoutId,
                    AssetId  = asset.Id,
                    X        = Math.Round(cx, 2),
                    Y        = Math.Round(cy, 2),
                    Scale    = 1.0,
                    Visible  = true,
                });
            }
            if (assetPositions.Count > 0)
                await layoutDb.UpsertAssetPositionBatchAsync(assetPositions);
            result.AssetPositionsImported = assetPositions.Count;

            // ── 그룹 → TwmsPlacementGroup + 멤버 ──
            // 매핑된 레이아웃들의 기존 그룹 삭제
            foreach (var layoutId in lineToLayoutId.Values.Distinct())
                await layoutDb.DeleteAllPlacementGroupsAsync(layoutId);

            foreach (var grp in groupRows)
            {
                // 멤버 자산들의 lineId로 소속 라인 역추적
                // (마스터 assetId는 "레이아웃" 라인에 있어 실제 소속과 다름)
                int? memberLineId = null;
                if (!string.IsNullOrWhiteSpace(grp.Assets))
                {
                    foreach (var s in grp.Assets.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(s.Trim(), out var mid) && allAssetLineMap.TryGetValue(mid, out var lid))
                        {
                            memberLineId = lid;
                            break;
                        }
                    }
                }
                // 멤버가 없으면 마스터 assetId의 lineId로 fallback
                if (!memberLineId.HasValue && allAssetLineMap.TryGetValue(grp.AssetId, out var fallbackId))
                    memberLineId = fallbackId;

                if (!memberLineId.HasValue
                    || !lineToLayoutId.TryGetValue(memberLineId.Value, out var grpLayoutId)
                    || !layoutTransform.TryGetValue(grpLayoutId, out var glt))
                {
                    result.Skipped++;
                    continue;
                }

                // 그룹 좌표도 도면 라인의 selfW x selfH 좌표 → viewBox 매핑
                var gx = (grp.X / glt.refW) * glt.imgAreaW + glt.offsetX;
                var gy = (grp.Y / glt.refH) * glt.imgAreaH + glt.offsetY;
                var gw = (grp.W / glt.refW) * glt.imgAreaW;
                var gh = (grp.H / glt.refH) * glt.imgAreaH;

                var newGroup = new TwmsPlacementGroup
                {
                    LayoutId = grpLayoutId,
                    Name     = "",
                    X        = Math.Round(Math.Clamp(gx, 0, VB_W), 2),
                    Y        = Math.Round(Math.Clamp(gy, 0, VB_H), 2),
                    Width    = Math.Round(Math.Max(gw, 10), 2),
                    Height   = Math.Round(Math.Max(gh, 10), 2),
                    Floor    = grp.Floor ?? 1,
                };
                var newGroupId = await layoutDb.InsertPlacementGroupAsync(newGroup);

                // 멤버 파싱
                if (!string.IsNullOrWhiteSpace(grp.Assets))
                {
                    var memberIds = grp.Assets.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
                        .Where(v => v > 0)
                        .ToList();
                    if (memberIds.Count > 0)
                        await layoutDb.SetPlacementGroupMembersAsync(newGroupId, memberIds);
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

    /// <summary>레이아웃에 연결된 실제 도면 이미지의 크기를 읽는다.</summary>
    private async Task<(double w, double h)> GetActualImageSizeAsync(int layoutId)
    {
        var config = await layoutDb.GetBlueprintConfigAsync(layoutId);
        if (config == null || string.IsNullOrEmpty(config.ImagePath))
            return (0, 0);

        // DB에 저장된 크기가 있으면 사용
        if (config.ImageWidth is > 0 && config.ImageHeight is > 0)
            return (config.ImageWidth.Value, config.ImageHeight.Value);

        // 없으면 파일에서 PNG 헤더 읽기
        var filePath = Path.Combine(webEnv.WebRootPath, config.ImagePath);
        if (!File.Exists(filePath)) return (0, 0);

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            // PNG: width at offset 16 (4 bytes BE), height at offset 20 (4 bytes BE)
            if (bytes.Length > 24 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (w, h);
            }
        }
        catch { /* ignore */ }
        return (0, 0);
    }

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
