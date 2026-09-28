using System.Globalization;
using System.Text;
using DEX.Core.Actor;
using Twms2.Dexa;
using Twms2.Server.HOCON;
using Twms2.Server.Models.Dexa;

namespace Twms2.Server.Services;

/// <summary>
/// 자산 관리: DEXA SQLite 직접 읽기 + 쓰기는 DEXA 서버 경유(Akka).
/// 쓰기를 서버에 맡겨야 자산명 중복 검사·AssetConfiguration 플러그인·
/// 접속 클라이언트 브로드캐스트가 함께 적용된다.
/// </summary>
public class AssetService
{
    private readonly DexaReadService _dexaRead;
    private readonly TwmDbService _twmDb;
    private readonly LayoutDbService _layoutDb;
    private readonly DexaServerClient _dexa;
    private readonly PingDbService _pingDb;
    private readonly ILogger<AssetService> _logger;

    public AssetService(DexaReadService dexaRead, TwmDbService twmDb, LayoutDbService layoutDb, DexaServerClient dexa, PingDbService pingDb, ILogger<AssetService> logger)
    {
        _dexaRead = dexaRead;
        _twmDb = twmDb;
        _layoutDb = layoutDb;
        _dexa = dexa;
        _pingDb = pingDb;
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
        // 서버 경유 저장은 자산명 중복 검사에 부모 ID 를 요구한다 (캐시된 목록에서 조회)
        var parentIds = (await _dexaRead.GetViewAssetsAsync())
            .GroupBy(a => a.AssetId)
            .ToDictionary(g => g.Key, g => g.First().AssetParentId);

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
                parentIds.TryGetValue(assetId, out var parentId);
                await SaveAssetParameterAsync(assetId, parentId, newParam, newAgent);
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

        if (anySuccess) InvalidateAssetCache();
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

        if (anySuccess) InvalidateAssetCache();
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
                    await SaveAssetParameterAsync(row.AssetId, row.AssetParentId, newParam, row.Agent);
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

        if (anyDexaChanged) InvalidateAssetCache();
        return results;
    }

    /// <summary>
    /// DEXA 자산 description 업데이트 (parameter HOCON 내 description 필드)
    /// </summary>
    public async Task UpdateDescriptionAsync(int dexaAssetId, string description)
    {
        await _dexaRead.UpdateAssetDescriptionAsync(dexaAssetId, description);
        InvalidateAssetCache();
    }

    // ── 자산 등록 ────────────────────────────────────────────

    /// <summary>등록 시 만드는 타입 폴더 이름. 기존 트리 관례(PLC/HMI/INV)를 따른다.</summary>
    private static string TypeFolderName(int assetTypeId) => assetTypeId switch
    {
        4 => "INV",
        5 => "HMI",
        6 => "PLC",
        7 => "SERVO",
        _ => throw new ArgumentException($"등록을 지원하지 않는 자산 타입입니다: {assetTypeId}"),
    };

    /// <summary>경로 세그먼트로 쓸 수 없는 문자를 치환. 라인명 'E/ROOM' 같은 값이 실제로 있다.</summary>
    private static string SanitizeSegment(string name)
    {
        var cleaned = new string((name ?? "").Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        return cleaned.Trim().TrimEnd('.');
    }

    public record RegisterAssetRequest(
        int AssetTypeId,
        string Name,
        int LineId,
        string? Ip = null,
        string? Description = null,
        string? Agent = null,
        string? ProjectFileToken = null);

    /// <summary>프로젝트 파일이 반드시 있어야 하는 타입 (PLC, 서보).</summary>
    public static bool RequiresProjectFile(int assetTypeId) => assetTypeId is 6 or 7;

    /// <summary>업로드된 프로젝트 파일 임시 보관 위치.</summary>
    public static string ProjectTempDir => Path.Combine(TwmsDataPath.Base, "project-tmp");

    /// <summary>
    /// DEXA 자산 신규 등록 — CreateNewAsset(Akka) 경유. 상위 폴더부터 차례로 만든다.
    ///
    /// ImportAssets 는 폴더를 자동 생성해 주지만 쓰지 않는다. 그 핸들러가 전체 자산의
    /// 경로로 Dictionary 를 만드는데, 구버전 TWM 에서 올라온 환경에는 레이아웃 그룹용
    /// 동명 폴더가 남아 있어 거기서 그대로 터진다
    /// ("An item with the same key has already been added." — 실측 확인).
    /// CreateNewAsset 은 중복 검사에 Count() 를 써서 그 영향을 받지 않는다.
    ///
    /// 경로: /twms/{라인ID}_{정규화 라인명}/{타입폴더}/{자산명}
    /// 실패는 예외가 아니라 항목별 에러 문자열로 돌아온다(부분 성공이 정상 동작).
    /// </summary>
    public async Task<(bool Ok, string? Error, int? AssetId)> RegisterAssetAsync(RegisterAssetRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return (false, "자산명이 비어 있습니다.", null);

        // PLC/서보는 등록 시점에 프로젝트 파일이 없으면 **나중에 붙일 수 없다**
        // (교체 로직이 기존 projectFileId 를 재사용하는데 NULL 이면 깨진다).
        // 백업이 영영 불가능한 자산이 생기므로 여기서 막는다.
        if (RequiresProjectFile(req.AssetTypeId) && string.IsNullOrWhiteSpace(req.ProjectFileToken))
            return (false, "PLC·서보는 프로젝트 파일이 필요합니다. 먼저 업로드한 뒤 토큰을 함께 보내세요.", null);

        var types = await _dexaRead.GetAssetTypesAsync();
        var type = types.FirstOrDefault(t => t.Id == req.AssetTypeId);
        if (type is null)
            return (false, $"자산 타입 {req.AssetTypeId} 를 찾을 수 없습니다.", null);

        var lines = await _layoutDb.GetTwmsLayoutLineMapAsync();
        if (!lines.TryGetValue(req.LineId, out var lineName))
            return (false, $"라인 {req.LineId} 을 찾을 수 없습니다.", null);

        var all = await _dexaRead.GetViewAssetsAsync();

        // 전역 동명 경고 — 서버는 같은 폴더 안만 보므로 TWMS 가 넓게 확인한다.
        var dup = all.FirstOrDefault(a => a.IsRealAsset &&
            string.Equals((a.Name ?? "").Trim(), req.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (dup != null)
            return (false, $"같은 이름의 자산이 이미 있습니다 (ID {dup.AssetId}).", null);

        // 폴더 체인: 루트 → twms → {ID}_{라인명} → {타입폴더}
        var rootAsset = all.FirstOrDefault(a => a.AssetTypeId == 1);
        if (rootAsset is null)
            return (false, "DEXA 루트 폴더를 찾을 수 없습니다.", null);
        var rootId = rootAsset.AssetId;
        var lineSeg = FindLineFolderName(all, req.LineId)
                      ?? $"{req.LineId:D2}_{SanitizeSegment(lineName)}";

        int twmsId, lineId2, typeId2;
        try
        {
            twmsId  = await EnsureFolderAsync(rootId, RootFolderName);
            lineId2 = await EnsureFolderAsync(twmsId, lineSeg);
            typeId2 = await EnsureFolderAsync(lineId2, TypeFolderName(req.AssetTypeId));
        }
        catch (DexaServerException ex)
        {
            return (false, $"폴더 생성 실패: {ex.Message}", null);
        }

        // 프로젝트 파일: 등록이 확인될 때까지 임시 파일을 지우지 않는다(실패 시 재시도 가능).
        string? tempPath = null, projectFileName = null;
        byte[]? projectBytes = null;
        if (!string.IsNullOrWhiteSpace(req.ProjectFileToken))
        {
            tempPath = FindProjectTempFile(req.ProjectFileToken!);
            if (tempPath is null)
                return (false, "업로드한 프로젝트 파일을 찾을 수 없습니다. 다시 업로드해 주세요.", null);

            projectBytes = await File.ReadAllBytesAsync(tempPath);
            // projectFile.path 는 NVARCHAR(128) — 원본 파일명만 넣는다.
            projectFileName = Path.GetFileName(tempPath).Split("__", 2).Last();
        }

        // 타입 템플릿에서 파라미터 생성
        var p = new Parameter(type.Parameter);
        SetItem(p, "name", req.Name);
        if (!string.IsNullOrWhiteSpace(req.Ip)) SetItem(p, "IP", req.Ip!);
        if (req.Description != null) SetItem(p, "description", req.Description);
        // DEXA GUI 는 이 필드로 프로젝트 파일 경로를 표시하고 편집 시 거기서 파일을 읽는다.
        // 템플릿 기본값(C:\Temp\drive.xgwx)을 두면 존재하지 않는 경로가 남는다.
        if (projectFileName != null) SetItem(p, "project", projectFileName);
        var parameter = Parameter.Buildup(p.Items.SelectMany(it => it.ToKeyValuePairs()));

        var create = new AmC2SRequestCreateNewAsset(req.AssetTypeId, req.Agent, parameter, typeId2);
        if (projectBytes != null)
        {
            create.ProjectFileContents = projectBytes;
            create.ProjectFileChecksum = Md5Hex(projectBytes);
            create.ProjectPath = projectFileName;
        }

        var reply = await _dexa.AskOrThrowAsync<AmS2CReplyRegisterAsset>(
            create, TimeSpan.FromSeconds(180));
        if (reply is null)
            return (false, "DEXA 서버 응답이 없습니다.", null);

        // 서버가 성공을 보고해도 실제 반영은 재조회로 확인한다.
        _dexaRead.InvalidateCache();
        var created = (await _dexaRead.GetViewAssetsAsync())
            .FirstOrDefault(a => a.AssetParentId == typeId2 &&
                string.Equals(a.Name, req.Name, StringComparison.Ordinal));

        if (created is null)
            return (false, "등록 응답은 성공이지만 DB 에서 자산을 찾지 못했습니다.", null);

        // TWMS 확장정보: 라인 배정. DEXA 경로는 등록 시점 스냅샷이고,
        // 화면의 라인 표시는 TwmsAsset.AugLineId 를 본다 — 둘 다 써야 한다.
        //
        // 실패해도 등록을 실패로 돌리지 않는다 — DEXA 자산은 이미 만들어졌고,
        // 여기서 예외를 올리면 "실패했다"면서 실제로는 자산이 생긴 상태가 남는다.
        // 라인은 나중에 자산 편집에서 다시 지정할 수 있다.
        try
        {
            await _twmDb.UpsertTwmsAssetAsync(new Models.Twm.TwmsAsset
            {
                DexaId    = created.AssetId,
                AugLineId = req.LineId,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "자산 {AssetId} 등록됨, 라인 배정 실패 — 편집에서 라인을 다시 지정해야 함", created.AssetId);
        }

        // 등록이 DB 로 확인된 뒤에야 임시 파일을 지운다.
        if (tempPath is not null)
        {
            try { File.Delete(tempPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "임시 프로젝트 파일 삭제 실패: {Path}", tempPath); }
        }

        return (true, null, created.AssetId);
    }

    /// <summary>토큰으로 임시 파일 찾기. 파일명은 "{토큰}__{원본이름}" 형식.</summary>
    public static string? FindProjectTempFile(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(Path.GetInvalidFileNameChars().Contains))
            return null;
        if (!Directory.Exists(ProjectTempDir)) return null;
        return Directory.EnumerateFiles(ProjectTempDir, token + "__*").FirstOrDefault();
    }

    /// <summary>DEXA 가 쓰는 체크섬 형식 — MD5 소문자 hex.</summary>
    public static string Md5Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// DEXA 자산/폴더 삭제 — Akka 경유(DeleteAssetById).
    ///
    /// 서버는 soft delete(deleted=1)만 하고 **자식을 따라가지 않는다.**
    /// 그래서 자식을 모아 깊은 것부터 지운다 — 부모를 먼저 지우면 고아가 남는다.
    /// 백업 파일과 이력은 보존되고, 라이선스 슬롯은 반환된다(카운트가 deleted=0 만 센다).
    /// 서버가 내부 예외를 삼키고도 성공 응답을 보내므로 건마다 재조회로 확인한다.
    /// </summary>
    public async Task<(bool Ok, string? Error, int Deleted)> DeleteAssetAsync(
        int assetId, bool includeChildren = false)
    {
        var all = await _dexaRead.GetViewAssetsAsync();
        var target = all.FirstOrDefault(a => a.AssetId == assetId);
        if (target is null)
            return (false, $"자산 {assetId} 를 찾을 수 없습니다.", 0);
        if (target.IsSystemRootFolder)
            return (false, "시스템 루트 폴더는 삭제할 수 없습니다.", 0);

        // 자식 먼저 담아 leaf-first 순서를 만든다.
        var order = new List<ViewAsset>();
        void Collect(ViewAsset node)
        {
            foreach (var child in all.Where(a => a.AssetParentId == node.AssetId))
                Collect(child);
            order.Add(node);
        }
        Collect(target);

        if (order.Count > 1 && !includeChildren)
            return (false, $"하위에 {order.Count - 1}개가 있습니다. 함께 지우려면 includeChildren 을 켜세요.", 0);

        var deleted = 0;
        foreach (var node in order)
        {
            var reply = await _dexa.AskOrThrowAsync<AmS2CReplyDeleteAsset>(
                new AmC2SRequestDeleteAssetById(node.AssetId), TimeSpan.FromSeconds(60));
            if (reply is null)
                return (false, $"자산 {node.AssetId} 삭제: DEXA 서버 응답이 없습니다.", deleted);

            _dexaRead.InvalidateCache();
            if ((await _dexaRead.GetViewAssetsAsync()).Any(a => a.AssetId == node.AssetId))
                return (false, $"자산 {node.AssetId}: 삭제 응답은 성공이지만 DB 에 그대로 남아 있습니다.", deleted);

            await CleanupTwmsRowsAsync(node.AssetId);
            deleted++;
        }

        return (true, null, deleted);
    }

    /// <summary>
    /// 자산이 사라진 뒤 TWMS 쪽에 남는 행 정리(확장정보·연결정보·도면배치·핑상태).
    ///
    /// 실패해도 삭제를 중단하지 않는다 — DEXA 삭제는 이미 커밋됐고 되돌릴 수 없는데,
    /// 여기서 예외를 올리면 "실패했다"고 보고하면서 실제로는 지워진 상태가 남는다.
    /// 남은 행은 자산이 없으니 조회에 걸리지 않아 무해하고, 나중에 정리할 수 있다.
    /// </summary>
    private async Task CleanupTwmsRowsAsync(int assetId)
    {
        try
        {
            await _twmDb.DeleteTwmsAssetAsync(assetId);
            await _twmDb.DeleteTwmsAssetConnAsync(assetId);
            await _layoutDb.DeleteAssetPlacementAsync(assetId);
            await _pingDb.DeletePingResultAsync(assetId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "자산 {AssetId} 삭제 후 TWMS 정리 실패 — DEXA 삭제는 완료됨. 남은 행은 수동 정리 필요",
                assetId);
        }
    }

    private const string RootFolderName = "twms";

    /// <summary>
    /// 같은 부모 아래 동명 폴더가 있으면 그 ID 를, 없으면 새로 만들어 ID 를 돌려준다.
    /// 폴더는 TypeId=2 라 서버의 라이선스 검사를 타지 않는다.
    /// </summary>
    private async Task<int> EnsureFolderAsync(int parentId, string folderName)
    {
        var existing = (await _dexaRead.GetViewAssetsAsync())
            .FirstOrDefault(a => a.IsFolder && a.AssetParentId == parentId &&
                string.Equals(a.Name, folderName, StringComparison.Ordinal));
        if (existing != null) return existing.AssetId;

        // DEXA GUI 가 쓰는 폴더 파라미터 형식(키는 임의, 값이 이름)
        var quoted = (char)34 + folderName + (char)34;
        var parameter = string.Join(Environment.NewLine,
            "Folder1.name.type = String",
            "Folder1.name.value = " + quoted);
        var reply = await _dexa.AskOrThrowAsync<AmS2CReplyRegisterAsset>(
            new AmC2SRequestCreateNewAsset(2, null, parameter, parentId),
            TimeSpan.FromSeconds(60));
        if (reply is null)
            throw new InvalidOperationException($"폴더 '{folderName}' 생성: DEXA 서버 응답이 없습니다.");

        _dexaRead.InvalidateCache();
        var created = (await _dexaRead.GetViewAssetsAsync())
            .FirstOrDefault(a => a.IsFolder && a.AssetParentId == parentId &&
                string.Equals(a.Name, folderName, StringComparison.Ordinal));
        return created?.AssetId
               ?? throw new InvalidOperationException($"폴더 '{folderName}' 를 만들었지만 DB 에서 찾지 못했습니다.");
    }

    /// <summary>라인 폴더는 "{ID}_" 접두로 찾아 재사용한다 (라인 이름이 바뀌어도 쪼개지지 않게).</summary>
    private static string? FindLineFolderName(List<ViewAsset> all, int lineId)
    {
        var twms = all.FirstOrDefault(a => a.IsFolder &&
            string.Equals(a.Name, RootFolderName, StringComparison.Ordinal));
        if (twms is null) return null;
        var prefix = $"{lineId:D2}_";
        return all.FirstOrDefault(a => a.IsFolder && a.AssetParentId == twms.AssetId &&
            (a.Name ?? "").StartsWith(prefix, StringComparison.Ordinal))?.Name;
    }

    private static void SetItem(Parameter p, string key, string value)
    {
        var item = p.Items.FirstOrDefault(it =>
            it.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && it.DataType != DataType.Button);
        if (item != null) item.Value = value;
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
    /// DEXA 자산의 parameter/agentPreferences 저장 — DEXA 서버 경유(Akka).
    /// 직접 SQL 과 달리 서버가 자산명 중복 검사 · AssetConfiguration 플러그인 실행 ·
    /// 접속 중인 전 클라이언트 브로드캐스트까지 처리한다.
    ///
    /// 거부되면 사유를 담은 DexaServerException 이 올라온다(라이선스 만료·이름 중복·서버 Lock).
    /// DEXA 서버가 떠 있어야 한다 — 직접 SQL 시절과 달리 서버 없이는 저장할 수 없다.
    /// 주의: 플러그인이 parameter 를 덧붙일 수 있어 저장 결과가 보낸 값과 다를 수 있다.
    /// </summary>
    private async Task SaveAssetParameterAsync(
        int assetId, int parentId, string parameter, string? agentPreferences)
    {
        var vwAsset = new DEX.Core.Database.ORM.ViewAsset
        {
            AssetId               = assetId,
            AssetParentId         = parentId,
            AssetParameter        = parameter,
            AssetAgentPreferences = agentPreferences,
        };

        var reply = await _dexa.AskOrThrowAsync<AmS2CReplyUpdateAssetParameter>(
            new AmC2SRequestUpdateAssetParameter(vwAsset));

        if (reply is null)
            throw new InvalidOperationException($"자산 {assetId} 저장: DEXA 서버 응답이 없습니다.");
    }

    /// <summary>
    /// 저장 직후 DEXA 자산 캐시 무효화.
    /// 서버도 DataChanged 를 브로드캐스트하지만 도착이 비동기라,
    /// 바로 이어지는 재조회가 옛 값을 보지 않도록 직접 비운다.
    /// </summary>
    private void InvalidateAssetCache() => _dexaRead.InvalidateCache();
}

public class CsvImportResult
{
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int NotFound { get; set; }
    public string? Error { get; set; }
    public bool HasError => Error != null;
}
