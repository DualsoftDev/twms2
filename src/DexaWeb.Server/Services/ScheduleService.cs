using DEX.Core.Actor;
using DexaWeb.Server.Models.Dexa;
using ORM = DEX.Core.Database.ORM;

namespace DexaWeb.Server.Services;

/// <summary>
/// 스케줄/트리거 관리.
/// 읽기: DEXA SQLite 직접 조회 (DexaReadService)
/// 쓰기/실행: DEXA Server Akka 메시징 (DexaServerClient)
/// </summary>
public class ScheduleService
{
    private readonly DexaReadService _dexaRead;
    private readonly DexaServerClient _dexa;
    private readonly ILogger<ScheduleService> _logger;

    public ScheduleService(DexaReadService dexaRead, DexaServerClient dexa, ILogger<ScheduleService> logger)
    {
        _dexaRead = dexaRead;
        _dexa = dexa;
        _logger = logger;
    }

    // ── 읽기 (SQLite) ──

    public async Task<List<Trigger>> GetTriggersAsync()
    {
        return await _dexaRead.GetTriggersAsync();
    }

    public async Task<List<Schedule>> GetSchedulesAsync()
    {
        return await _dexaRead.GetSchedulesAsync();
    }

    // ── 쓰기/실행 (Akka) ──

    public async Task<bool> AddTriggerAsync(string name, string cronExpression, string? description)
    {
        var trigger = new ORM.Trigger(null, name, cronExpression, description);
        var reply = await _dexa.AskAsync<AmS2CReplyAddTrigger>(
            new AmC2SRequestAddTrigger(trigger));
        return reply != null;
    }

    public async Task<bool> UpdateTriggerAsync(int id, string name, string cronExpression, bool enabled, string? description)
    {
        var trigger = new ORM.Trigger(id, name, cronExpression, description, enable: enabled);
        var reply = await _dexa.AskAsync<AmS2CReplyUpdateTrigger>(
            new AmC2SRequestUpdateTrigger(trigger));
        return reply != null;
    }

    public async Task<bool> DeleteTriggerAsync(int id)
    {
        var trigger = new ORM.Trigger(id, null, null, null);
        var reply = await _dexa.AskAsync<AmS2CReplyDeleteTrigger>(
            new AmC2SRequestDeleteTrigger(trigger));
        return reply != null;
    }

    public async Task<bool> ExecuteTriggerAsync(int id)
    {
        var reply = await _dexa.AskAsync<AmS2CReplyExecuteTriggerOnce>(
            new AmC2SExecuteTriggerOnce(id));
        return reply != null;
    }

    public async Task<bool> UpdateSchedulesAsync(int triggerId, int[] assetIds)
    {
        // SQLite에서 현재 스케줄 조회하여 변경사항 계산
        var currentSchedules = await _dexaRead.GetSchedulesAsync();

        var currentAssetIds = currentSchedules
            .Where(s => s.TriggerId == triggerId)
            .Select(s => s.AssetId)
            .ToHashSet();

        var targetAssetIds = assetIds.ToHashSet();

        var adds = targetAssetIds.Except(currentAssetIds)
            .Select(assetId => Tuple.Create(triggerId, assetId));
        var removes = currentAssetIds.Except(targetAssetIds)
            .Select(assetId => Tuple.Create(triggerId, assetId));

        var reply = await _dexa.AskAsync<AmS2CReplySchedulesChange>(
            new AmC2SRequestSchedulesChange(adds, removes));
        return reply != null;
    }
}
