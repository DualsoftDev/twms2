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

    public async Task<bool> UpdateTriggerCronAsync(int triggerId, string cronExpression)
    {
        var triggers = await _dexaRead.GetTriggersAsync();
        var existing = triggers.FirstOrDefault(t => t.Id == triggerId);
        if (existing == null) return false;

        return await UpdateTriggerAsync(existing.Id, existing.Name, cronExpression, existing.Enabled, existing.Description);
    }

    public async Task<bool> DeleteTriggerAsync(int id)
    {
        var trigger = new ORM.Trigger(id, null, null, null);
        var reply = await _dexa.AskAsync<AmS2CReplyDeleteTrigger>(
            new AmC2SRequestDeleteTrigger(trigger));
        return reply != null;
    }

    public Task<bool> ExecuteTriggerAsync(int id)
    {
        // Tell (fire-and-forget) — 트리거 백업은 오래 걸리므로 응답을 기다리지 않음
        // 완료 알림은 서버에서 DataChanged로 수신
        try
        {
            _dexa.Tell(new AmC2SExecuteTriggerOnce(id));
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "트리거 실행 요청 실패 (triggerId={TriggerId})", id);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> UpdateSchedulesAsync(int triggerId, int[] assetIds)
    {
        try
        {
            var currentSchedules = await _dexaRead.GetSchedulesAsync();

            var currentAssetIds = currentSchedules
                .Where(s => s.TriggerId == triggerId)
                .Select(s => s.AssetId)
                .ToHashSet();

            var targetAssetIds = assetIds.ToHashSet();

            foreach (var assetId in targetAssetIds.Except(currentAssetIds))
                await _dexaRead.AddScheduleAsync(triggerId, assetId);

            foreach (var assetId in currentAssetIds.Except(targetAssetIds))
                await _dexaRead.RemoveScheduleAsync(triggerId, assetId);

            // UpdateTrigger 메시지를 보내 DEXA Server의 Quartz 리스케줄링 유도
            // (DataChanged → ScheduleFromDatabase() → DB에서 전체 스케줄 다시 읽음)
            var triggers = await _dexaRead.GetTriggersAsync();
            var trigger = triggers.FirstOrDefault(t => t.Id == triggerId);
            if (trigger != null)
            {
                await UpdateTriggerAsync(trigger.Id, trigger.Name, trigger.CronExpression,
                    trigger.Enabled, trigger.Description);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "스케줄 매핑 업데이트 실패 (triggerId={TriggerId})", triggerId);
            return false;
        }
    }
}
