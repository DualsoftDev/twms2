using System.Globalization;
using System.Text;
using DEX.Core.Actor;
using Twms2.Server.HOCON;
using Twms2.Server.Models.Dexa;

namespace Twms2.Server.Services;

/// <summary>
/// 자산 관리: DEXA SQLite 직접 읽기/쓰기.
/// DB 수정 후 DEXA Server에 알림을 보내 연결된 클라이언트에 변경 브로드캐스트.
/// </summary>
public class AssetService
{
    private readonly DexaReadService _dexaRead;
    private readonly TwmDbService _twmDb;
    private readonly LayoutDbService _layoutDb;
    private readonly DexaServerClient _dexa;
    private readonly ILogger<AssetService> _logger;

    public AssetService(DexaReadService dexaRead, TwmDbService twmDb, LayoutDbService layoutDb, DexaServerClient dexa, ILogger<AssetService> logger)
    {
        _dexaRead = dexaRead;
        _twmDb = twmDb;
        _layoutDb = layoutDb;
        _dexa = dexa;
        _logger = logger;
    }

    /// <summary>
    /// DEXA SQLite에서 전체 자산 조회 + TWM aug 데이터 조합.
    /// PingService, AssetStatusService 등에서도 공통으로 사용.
    /// </summary>
    public async Task<List<ViewAsset>> GetMergedAssetsAsync()
    {
        var assets = await _dexaRead.GetViewAssetsAsync();
        await ApplyAugDataAsync(assets);
        return assets;
    }

    /// <summary>GetMergedAssetsAsync()의 기존 별칭 (동일 기능)</summary>
    public Task<List<ViewAsset>> GetAllAssetsAsync() => GetMergedAssetsAsync();

    /// <summary>
    /// ViewAsset 목록에 TwmsAsset (공통) + TwmsAssetConn (연결정보) 병합
    /// </summary>
    private async Task ApplyAugDataAsync(List<ViewAsset> assets)
    {
        try
        {
            var augTask  = _twmDb.GetTwmsAssetMapAsync();
            var connTask = _twmDb.GetTwmsAssetConnMapAsync();
            var lineTask = _layoutDb.GetTwmsLayoutLineMapAsync();
            await Task.WhenAll(augTask, connTask, lineTask);

            var augMap  = augTask.Result;
            var connMap = connTask.Result;
            var lineMap = lineTask.Result;

            if (augMap.Count == 0 && connMap.Count == 0) return;

            foreach (var asset in assets)
            {
                augMap.TryGetValue(asset.AssetId, out var aug);
                connMap.TryGetValue(asset.AssetId, out var conn);
                if (aug == null && conn == null) continue;
                asset.ApplyAug(aug, conn);

                if (asset.AugLineId.HasValue && lineMap.TryGetValue(asset.AugLineId.Value, out var lineName))
                    asset.LayoutLineName = lineName;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aug 데이터 조합 실패 — DEXA 데이터만 사용");
        }
    }

    /// <summary>
    /// Aug 필드 수정 (TwmsAsset 저장).
    /// IP/Name 등은 DEXA parameter에서 직접 관리하므로 동기화 불필요.
    /// </summary>
    public async Task UpdateAugFieldsAsync(int assetId, Models.Twm.TwmsAsset aug)
    {
        await _twmDb.UpsertTwmsAssetAsync(aug);
    }

    /// <summary>
    /// DEXA SQLite에서 자산 타입 목록 조회
    /// </summary>
    public async Task<List<AssetType>> GetAssetTypesAsync()
    {
        return await _dexaRead.GetAssetTypesAsync();
    }

    /// <summary>
    /// 복수 자산 일괄 수정 (모든 필드를 DEXA parameter HOCON에 기록).
    /// </summary>
    public async Task<List<BatchUpdateResult>> BatchUpdateAssetsAsync(
        int[] assetIds,
        BatchEditSpec spec,
        IProgress<(int completed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!spec.HasChanges)
            return assetIds.Select(id => new BatchUpdateResult { AssetId = id, Success = true }).ToList();

        var rawAssets = await _dexaRead.GetAssetRawBatchAsync(assetIds);

        var results = new List<BatchUpdateResult>();
        int completed = 0;
        int total = assetIds.Length;
        bool anySuccess = false;

        foreach (var assetId in assetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!rawAssets.TryGetValue(assetId, out var raw))
                {
                    results.Add(new BatchUpdateResult
                    {
                        AssetId = assetId,
                        Success = false,
                        ErrorMessage = $"자산 ID {assetId}를 DB에서 찾을 수 없습니다."
                    });
                    completed++;
                    progress?.Report((completed, total));
                    continue;
                }

                var newParam = ApplySpecToParameter(raw.parameter, spec);
                var newAgent = spec.AgentPreferences ?? raw.agentPreferences;
                await _dexaRead.UpdateAssetAsync(assetId, newParam, newAgent);
                anySuccess = true;

                results.Add(new BatchUpdateResult { AssetId = assetId, Success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "자산 {AssetId} 배치 업데이트 실패", assetId);
                results.Add(new BatchUpdateResult
                {
                    AssetId = assetId,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }

            completed++;
            progress?.Report((completed, total));
        }

        if (anySuccess) NotifyAssetChanged();
        return results;
    }

    /// <summary>
    /// BatchEditSpec을 HOCON parameter 문자열에 적용하여 새 문자열 반환.
    /// 기존 아이템 업데이트 + 없는 아이템 신규 추가.
    /// </summary>
    private static string ApplySpecToParameter(string param, BatchEditSpec spec)
    {
        if (string.IsNullOrEmpty(param))
            return param;

        try
        {
            var hoconParam = new Parameter(param);

            // 기존 아이템 값 업데이트 (존재하는 경우만)
            UpdateItemValue(hoconParam, "name", spec.Name);
            UpdateItemValue(hoconParam, "IP", spec.Ip, ignoreCase: true);
            UpdateItemValue(hoconParam, "description", spec.Description);
            UpdateItemValue(hoconParam, "via1_connection", spec.ViaIp, ignoreCase: true);
            UpdateItemValue(hoconParam, "via1_base", spec.BaseNumber?.ToString(), ignoreCase: true);
            UpdateItemValue(hoconParam, "via1_slot", spec.SlotNumber?.ToString(), ignoreCase: true);

            // 기존 아이템에서 flat kv pairs 수집
            var tpls = hoconParam.Items.SelectMany(it => it.ToKeyValuePairs()).ToList();

            // 없는 아이템은 신규 추가
            if (spec.ViaIp != null)
                AppendIfMissing(hoconParam, tpls, "via1_connection", spec.ViaIp);
            if (spec.BaseNumber != null)
                AppendIfMissing(hoconParam, tpls, "via1_base", spec.BaseNumber.Value.ToString());
            if (spec.SlotNumber != null)
                AppendIfMissing(hoconParam, tpls, "via1_slot", spec.SlotNumber.Value.ToString());

            return Parameter.Buildup(tpls);
        }
        catch
        {
            // HOCON 파싱 실패 시 regex fallback
            var result = param;
            if (spec.Name != null) result = ParameterHelper.ReplaceValueFallback(result, @"\.name\.value", spec.Name);
            if (spec.Ip != null) result = ParameterHelper.ReplaceValueFallback(result, @"\.IP\.value", spec.Ip);
            if (spec.Description != null) result = ParameterHelper.ReplaceValueFallback(result, @"\.description\.value", spec.Description);
            if (spec.ViaIp != null) result = ParameterHelper.ReplaceValueFallback(result, @"\.via1_connection\.value", spec.ViaIp);
            if (spec.BaseNumber != null) result = ParameterHelper.ReplaceValueFallback(result, @"\.via1_base\.value", spec.BaseNumber.Value.ToString());
            if (spec.SlotNumber != null) result = ParameterHelper.ReplaceValueFallback(result, @"\.via1_slot\.value", spec.SlotNumber.Value.ToString());
            return result;
        }
    }

    /// <summary>기존 ParameterItem 값 업데이트 (아이템이 존재하는 경우에만)</summary>
    private static void UpdateItemValue(Parameter hoconParam, string key, string? newValue, bool ignoreCase = false)
    {
        if (newValue == null) return;
        var item = hoconParam.Items.FirstOrDefault(it =>
            ignoreCase ? it.Key.Equals(key, StringComparison.OrdinalIgnoreCase) : it.Key == key);
        if (item != null) item.Value = newValue;
    }

    /// <summary>아이템이 존재하지 않으면 flat kv pairs에 신규 추가</summary>
    private static void AppendIfMissing(Parameter hoconParam, List<(string Key, string Value)> tpls, string key, string value)
    {
        var exists = hoconParam.Items.Any(it =>
            it.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (exists) return;

        var at = hoconParam.AssetTypeName;
        tpls.Add(($"{at}.{key}.type", "String"));
        tpls.Add(($"{at}.{key}.value", ParameterHelper.WrapQuoteOnDemand(ParameterHelper.Escape(value))));
    }

    /// <summary>
    /// DEXA SQLite에서 편집 가능한 자산 목록 조회.
    /// 삭제되지 않은 자산 중 폴더 제외, 실제 자산만.
    /// </summary>
    public async Task<List<EditableAsset>?> GetEditableAssetsAsync()
    {
        var assets = await _dexaRead.GetViewAssetsAsync();
        return assets
            .Where(va => va.IsRealAsset)
            .Select(EditableAsset.FromViewAsset)
            .ToList();
    }

    /// <summary>
    /// 수정된 EditableAsset 목록을 DEXA SQLite에 직접 저장.
    /// </summary>
    public async Task<List<BatchUpdateResult>> UpdateEditableAssetsAsync(
        IEnumerable<EditableAsset> modifiedAssets,
        IProgress<(int completed, int total)>? progress = null)
    {
        var targets = modifiedAssets.ToList();
        if (targets.Count == 0) return [];

        var results = new List<BatchUpdateResult>();
        int completed = 0;
        int total = targets.Count;
        bool anySuccess = false;

        foreach (var asset in targets)
        {
            try
            {
                var newParam = asset.BuildParameter();
                await _dexaRead.UpdateAssetAsync(asset.AssetId, newParam, asset.AgentPreferences);

                results.Add(new BatchUpdateResult
                {
                    AssetId = asset.AssetId,
                    Success = true
                });
                anySuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "자산 {AssetId} 업데이트 실패", asset.AssetId);
                results.Add(new BatchUpdateResult
                {
                    AssetId = asset.AssetId,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }

            completed++;
            progress?.Report((completed, total));
        }

        if (anySuccess) NotifyAssetChanged();
        return results;
    }

    /// <summary>
    /// 탭 편집용 자산 행 목록 (DEXA + TWM aug/conn 통합).
    /// </summary>
    public async Task<List<AssetEditRow>> GetAssetEditRowsAsync()
    {
        var assetsTask = _dexaRead.GetViewAssetsAsync();
        var augTask    = _twmDb.GetTwmsAssetMapAsync();
        var connTask   = _twmDb.GetTwmsAssetConnMapAsync();
        await Task.WhenAll(assetsTask, augTask, connTask);

        var augMap  = augTask.Result;
        var connMap = connTask.Result;

        return assetsTask.Result
            .Where(a => a.IsRealAsset)
            .Select(va =>
            {
                augMap.TryGetValue(va.AssetId, out var aug);
                connMap.TryGetValue(va.AssetId, out var conn);
                return AssetEditRow.From(va, aug, conn);
            })
            .ToList();
    }

    /// <summary>
    /// 수정된 AssetEditRow 일괄 저장 (DEXA parameter + TWM aug/conn).
    /// </summary>
    public async Task<List<(int AssetId, bool Success, string? Error)>> SaveAssetEditRowsAsync(
        IEnumerable<AssetEditRow> rows,
        IProgress<(int completed, int total)>? progress = null)
    {
        var targets = rows.Where(r => r.IsModified).ToList();
        var results = new List<(int, bool, string?)>();
        int completed = 0;
        bool anyDexaChanged = false;

        foreach (var row in targets)
        {
            try
            {
                bool isDrive = row.AssetTypeId == 4;
                bool needDexaSave = row.IsDexaModified || (isDrive && row.IsTwmConnModified);

                if (needDexaSave)
                {
                    row.DexaAsset.SetField("name", row.Name);
                    row.DexaAsset.SetField("IP", row.Ip);
                    row.DexaAsset.SetField("description", row.Description);

                    if (isDrive)
                    {
                        // nested HOCON format (connections.Via Connections.1 depth.*)
                        row.DexaAsset.SetField("connection", row.ConnIpVia ?? "");
                        row.DexaAsset.SetField("base number", row.ConnBase.ToString());
                        row.DexaAsset.SetField("slot number", row.ConnSlot?.ToString() ?? "");
                        row.DexaAsset.SetField("modelName", row.ModelName);
                        row.DexaAsset.SetField("modelVersion", row.ModelVersion);
                        // flat HOCON format fallback
                        row.DexaAsset.SetField("via1_connection", row.ConnIpVia ?? "");
                        row.DexaAsset.SetField("via1_base", row.ConnBase.ToString());
                        row.DexaAsset.SetField("via1_slot", row.ConnSlot?.ToString() ?? "");
                        // 경유 연결 활성 여부 (visible 플래그)
                        row.DexaAsset.SetSectionVisible(
                            "connections.Via Connections.1 depth",
                            row.ConnViaEnabled ? "True" : "false");
                    }

                    row.DexaAsset.AgentPreferences = row.Agent;
                    var newParam = row.DexaAsset.BuildParameter();
                    await _dexaRead.UpdateAssetAsync(row.AssetId, newParam, row.Agent);
                    anyDexaChanged = true;
                }

                if (row.IsTwmAugModified)
                {
                    await _twmDb.UpsertTwmsAssetAsync(new Models.Twm.TwmsAsset
                    {
                        DexaId           = row.AssetId,
                        AugStationNumber = row.StationNumber,
                        AugVendor        = row.Vendor,
                        AugSpec          = row.Spec,
                        AugLineId        = row.LineId,
                    });
                }

                // PLC/Servo: 연결정보는 TwmsAssetConn에 저장
                bool isPlcServo = row.AssetTypeId == 6 || row.AssetTypeId == 7;
                if (isPlcServo && row.IsTwmConnModified)
                {
                    await _twmDb.UpsertTwmsAssetConnAsync(new Models.Twm.TwmsAssetConn
                    {
                        DexaId        = row.AssetId,
                        AugIp         = row.ConnIp,
                        AugIpVia      = row.ConnIpVia,
                        AugBaseNumber = row.ConnBase,
                        AugSlotNumber = row.ConnSlot,
                        AugIsRobotPLC = row.ConnIsRobot,
                    });
                }

                row.MarkSaved();
                results.Add((row.AssetId, true, null));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "자산 {Id} 저장 실패", row.AssetId);
                results.Add((row.AssetId, false, ex.Message));
            }

            completed++;
            progress?.Report((completed, targets.Count));
        }

        if (anyDexaChanged) NotifyAssetChanged();
        return results;
    }

    /// <summary>
    /// DEXA 자산 description 업데이트 (parameter HOCON 내 description 필드)
    /// </summary>
    public async Task UpdateDescriptionAsync(int dexaAssetId, string description)
    {
        await _dexaRead.UpdateAssetDescriptionAsync(dexaAssetId, description);
        NotifyAssetChanged();
    }

    // ── CSV Export / Import ──────────────────────────────────

    private static readonly string[] CsvHeaders =
    [
        "AssetId", "AssetTypeId", "TypeName",
        "Name", "LineName", "StationNumber", "Vendor", "Spec",
        "DisplayIp", "ConnIpVia", "ConnBase", "ConnSlot", "ConnIsRobot",
        "Description", "Agent", "ModelName", "ModelVersion",
    ];

    /// <summary>
    /// AssetEditRow 목록을 BOM UTF-8 CSV 바이트 배열로 변환.
    /// </summary>
    public static byte[] ExportCsv(IReadOnlyList<AssetEditRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsvHeaders));

        foreach (var r in rows)
        {
            sb.Append(r.AssetId).Append(',');
            sb.Append(r.AssetTypeId).Append(',');
            sb.Append(CsvEscape(r.TypeName)).Append(',');
            sb.Append(CsvEscape(r.Name)).Append(',');
            sb.Append(CsvEscape(r.LineName)).Append(',');
            sb.Append(r.StationNumber?.ToString() ?? "").Append(',');
            sb.Append(CsvEscape(r.Vendor ?? "")).Append(',');
            sb.Append(CsvEscape(r.Spec ?? "")).Append(',');
            sb.Append(CsvEscape(r.DisplayIp)).Append(',');
            sb.Append(CsvEscape(r.ConnIpVia ?? "")).Append(',');
            sb.Append(r.ConnBase).Append(',');
            sb.Append(r.ConnSlot?.ToString() ?? "").Append(',');
            sb.Append(r.ConnIsRobot?.ToString() ?? "").Append(',');
            sb.Append(CsvEscape(r.Description)).Append(',');
            sb.Append(CsvEscape(r.Agent)).Append(',');
            sb.Append(CsvEscape(r.ModelName)).Append(',');
            sb.AppendLine(CsvEscape(r.ModelVersion));
        }

        // BOM + UTF-8
        var bom = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[bom.Length + body.Length];
        bom.CopyTo(result, 0);
        body.CopyTo(result, bom.Length);
        return result;
    }

    /// <summary>
    /// CSV 스트림을 파싱하여 기존 AssetEditRow에 값 적용.
    /// 빈 셀은 변경하지 않음. 존재하지 않는 AssetId는 무시.
    /// </summary>
    public static CsvImportResult ApplyCsvImport(
        List<AssetEditRow> allRows,
        Stream csvStream,
        Dictionary<int, string> lineMap)
    {
        var rowMap = allRows.ToDictionary(r => r.AssetId);
        var reverseLineMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in lineMap)
            reverseLineMap.TryAdd(name, id);

        using var reader = new StreamReader(csvStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine();
        if (headerLine == null)
            return new CsvImportResult { Error = "빈 파일입니다." };

        var headers = ParseCsvLine(headerLine);
        var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
            colIndex[headers[i].Trim()] = i;

        if (!colIndex.ContainsKey("AssetId"))
            return new CsvImportResult { Error = "AssetId 컬럼이 없습니다." };

        int updated = 0, skipped = 0, notFound = 0;
        int lineNum = 1;

        while (reader.ReadLine() is { } line)
        {
            lineNum++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = ParseCsvLine(line);
            if (!colIndex.TryGetValue("AssetId", out var idIdx) || idIdx >= cols.Length) { skipped++; continue; }
            if (!int.TryParse(cols[idIdx].Trim(), out var assetId)) { skipped++; continue; }
            if (!rowMap.TryGetValue(assetId, out var row)) { notFound++; continue; }

            bool changed = false;
            changed |= ApplyStr(cols, colIndex, "Name", v => row.Name = v);
            changed |= ApplyStr(cols, colIndex, "Description", v => row.Description = v);
            changed |= ApplyStr(cols, colIndex, "Agent", v => row.Agent = v);
            changed |= ApplyStr(cols, colIndex, "Vendor", v => row.Vendor = v);
            changed |= ApplyStr(cols, colIndex, "Spec", v => row.Spec = v);
            changed |= ApplyStr(cols, colIndex, "DisplayIp", v => row.DisplayIp = v);
            changed |= ApplyStr(cols, colIndex, "ConnIpVia", v => row.ConnIpVia = string.IsNullOrEmpty(v) ? null : v);
            changed |= ApplyStr(cols, colIndex, "ModelName", v => row.ModelName = v);
            changed |= ApplyStr(cols, colIndex, "ModelVersion", v => row.ModelVersion = v);

            // 숫자 필드
            changed |= ApplyNullableInt(cols, colIndex, "StationNumber", v => row.StationNumber = v);
            changed |= ApplyInt(cols, colIndex, "ConnBase", v => row.ConnBase = v);
            changed |= ApplyNullableInt(cols, colIndex, "ConnSlot", v => row.ConnSlot = v);
            changed |= ApplyNullableInt(cols, colIndex, "ConnIsRobot", v => row.ConnIsRobot = v);

            // LineName → LineId 역매핑
            if (colIndex.TryGetValue("LineName", out var lnIdx) && lnIdx < cols.Length)
            {
                var lnVal = cols[lnIdx].Trim();
                if (lnVal.Length > 0)
                {
                    if (reverseLineMap.TryGetValue(lnVal, out var lineId))
                    {
                        row.LineId = lineId;
                        row.LineName = lnVal;
                        changed = true;
                    }
                    // 매핑 실패 시 변경하지 않음
                }
            }

            if (changed) updated++;
            else skipped++;
        }

        return new CsvImportResult { Updated = updated, Skipped = skipped, NotFound = notFound };
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(""); break; }

            if (line[i] == '"')
            {
                // quoted field
                var sb = new StringBuilder();
                i++; // skip opening quote
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                        }
                        else
                        {
                            i++; // skip closing quote
                            break;
                        }
                    }
                    else
                    {
                        sb.Append(line[i]);
                        i++;
                    }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++; // skip comma
            }
            else
            {
                var next = line.IndexOf(',', i);
                if (next < 0)
                {
                    fields.Add(line[i..]);
                    break;
                }
                fields.Add(line[i..next]);
                i = next + 1;
            }
        }
        return fields.ToArray();
    }

    private static bool ApplyStr(string[] cols, Dictionary<string, int> colIndex, string key, Action<string> setter)
    {
        if (!colIndex.TryGetValue(key, out var idx) || idx >= cols.Length) return false;
        var val = cols[idx].Trim();
        if (val.Length == 0) return false; // 빈 셀 → 변경 안 함
        setter(val);
        return true;
    }

    private static bool ApplyInt(string[] cols, Dictionary<string, int> colIndex, string key, Action<int> setter)
    {
        if (!colIndex.TryGetValue(key, out var idx) || idx >= cols.Length) return false;
        var val = cols[idx].Trim();
        if (val.Length == 0) return false;
        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) { setter(n); return true; }
        return false;
    }

    private static bool ApplyNullableInt(string[] cols, Dictionary<string, int> colIndex, string key, Action<int?> setter)
    {
        if (!colIndex.TryGetValue(key, out var idx) || idx >= cols.Length) return false;
        var val = cols[idx].Trim();
        if (val.Length == 0) return false;
        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) { setter(n); return true; }
        return false;
    }

    /// <summary>
    /// DEXA Server에 DB 변경 알림 전송 (fire-and-forget).
    /// 서버가 연결된 모든 클라이언트에 변경 브로드캐스트.
    /// </summary>
    private void NotifyAssetChanged()
    {
        _dexa.Tell(new AmC2SNotifyDataChanged("asset", DatabaseChangeOperation.Update));
    }
}

public class CsvImportResult
{
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int NotFound { get; set; }
    public string? Error { get; set; }
    public bool HasError => Error != null;
}
