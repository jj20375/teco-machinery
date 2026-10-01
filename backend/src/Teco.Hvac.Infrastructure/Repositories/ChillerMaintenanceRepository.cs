using Dapper;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

public sealed class ChillerMaintenanceState
{
    public uint? BaselineHours { get; init; }
    public DateTime? BaselineAt { get; init; }
}

public sealed class ChillerMaintenanceLogRow
{
    public long Id { get; init; }
    public DateTime PerformedAt { get; init; }
    public uint PerformedBy { get; init; }
    public string? PerformedByName { get; init; }
    public uint HoursAtReset { get; init; }
    public uint? HoursSincePrevious { get; init; }
    public string? Memo { get; init; }
}

public sealed record ChillerMaintenanceResetResult(int? HoursSincePrevious, long? ClosedAlarmEventId);

/// <summary>
/// 冰水主機保養提醒的「機車換機油」模式：記住上次保養時的累積運轉時數（基準點），
/// 距上次保養 = 目前時數 − 基準點，達到保養間隔就亮燈（開一筆告警），直到按「保養完成」才熄燈並重新起算。
/// Collector（評估、開告警）與 API（重置）共用這支 repository。
/// </summary>
public sealed class ChillerMaintenanceRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 保養告警固定用這個 rule_code，不像其他門檻規則帶 ".{op}.{threshold}" 後綴——
    /// 改保養間隔不該讓已經亮著的燈熄掉再重開一次。
    /// </summary>
    public const string MaintenanceRuleCode = nameof(ChillerSnapshot.AccumulatedRunningHours);

    public async Task<ChillerMaintenanceState> GetStateAsync(int deviceId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ChillerMaintenanceState>(
            "SELECT maintenance_baseline_hours AS BaselineHours, maintenance_baseline_at AS BaselineAt " +
            "FROM device_chiller WHERE id = @deviceId", new { deviceId }) ?? new ChillerMaintenanceState();
    }

    /// <summary>
    /// 第一次開始計算時以當下時數當基準點。WHERE 條件限定基準點還是 NULL，
    /// 已經有人按過重置的話不會被覆蓋。
    /// </summary>
    public async Task InitBaselineIfMissingAsync(int deviceId, int hours, DateTimeOffset atUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE device_chiller SET maintenance_baseline_hours = @hours, maintenance_baseline_at = @at " +
            "WHERE id = @deviceId AND maintenance_baseline_hours IS NULL",
            new { deviceId, hours, at = atUtc.UtcDateTime });
    }

    /// <summary>主機計數器倒退（換控制板或歸零）時，把基準點改成目前時數，避免算出負數。</summary>
    public async Task RebaseAsync(int deviceId, uint expectedBaseline, int hours, DateTimeOffset atUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE device_chiller SET maintenance_baseline_hours = @hours, maintenance_baseline_at = @at " +
            "WHERE id = @deviceId AND maintenance_baseline_hours = @expectedBaseline",
            new { deviceId, expectedBaseline, hours, at = atUtc.UtcDateTime });
    }

    /// <summary>
    /// 只在「基準點仍是剛才讀到的值，且沒有尚未結束的保養告警」時才開告警，用一條 INSERT…SELECT 完成。
    /// 分成「先查再寫」兩步的話，Collector 讀完基準點後剛好有人按重置，就會在重置後馬上又開一筆告警。
    /// 回傳 true 代表這次真的開了告警（也就是發出通知）。
    /// </summary>
    public async Task<bool> OpenAlarmIfDueAsync(
        int deviceId, uint expectedBaseline, int hoursSinceService, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var affected = await conn.ExecuteAsync(
            """
            INSERT INTO alarm_event (device_type, device_id, rule_code, severity, started_at, peak_value)
            SELECT @deviceType, d.id, @ruleCode, @severity, @startedAt, @peakValue
            FROM device_chiller d
            WHERE d.id = @deviceId AND d.maintenance_baseline_hours = @expectedBaseline
              AND NOT EXISTS (
                  SELECT 1 FROM alarm_event e
                  WHERE e.device_type = @deviceType AND e.device_id = @deviceId
                    AND e.rule_code = @ruleCode AND e.ended_at IS NULL)
            """,
            new
            {
                deviceType = (int)AlarmDeviceType.Chiller, deviceId, expectedBaseline, ruleCode = MaintenanceRuleCode,
                severity = (int)AlarmSeverity.Info, startedAt = nowUtc.UtcDateTime, peakValue = (double)hoursSinceService,
            });
        return affected > 0;
    }

    /// <summary>主機離線時，重置改用最後一筆讀取成功的時數。</summary>
    public async Task<int?> GetLatestRecordedHoursAsync(int deviceId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(
            "SELECT running_hours FROM chiller_reading WHERE device_id = @deviceId AND read_status = 1 " +
            "AND running_hours IS NOT NULL ORDER BY ts DESC LIMIT 1", new { deviceId });
    }

    /// <summary>
    /// 「保養完成」：在同一個交易裡寫履歷、更新基準點、關閉保養告警（記錄由誰確認），
    /// 任何一步失敗就整個退回，不會出現燈熄了但基準點沒更新的狀態。
    /// </summary>
    public async Task<ChillerMaintenanceResetResult> ResetAsync(
        int deviceId, int currentHours, int userId, DateTimeOffset nowUtc, string? memo, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        using var tx = await conn.BeginTransactionAsync(ct);

        var baseline = await conn.ExecuteScalarAsync<uint?>(
            "SELECT maintenance_baseline_hours FROM device_chiller WHERE id = @deviceId FOR UPDATE",
            new { deviceId }, tx);
        int? sincePrevious = baseline is null ? null : Math.Max(0, currentHours - (int)baseline.Value);

        var activeAlarmId = await conn.ExecuteScalarAsync<long?>(
            "SELECT id FROM alarm_event WHERE device_type = @deviceType AND device_id = @deviceId " +
            "AND rule_code = @ruleCode AND ended_at IS NULL ORDER BY started_at DESC LIMIT 1",
            new { deviceType = (int)AlarmDeviceType.Chiller, deviceId, ruleCode = MaintenanceRuleCode }, tx);

        var at = nowUtc.UtcDateTime;
        await conn.ExecuteAsync(
            "INSERT INTO chiller_maintenance_log (device_id, performed_at, performed_by, hours_at_reset, hours_since_previous, alarm_event_id, memo) " +
            "VALUES (@deviceId, @at, @userId, @currentHours, @sincePrevious, @activeAlarmId, @memo)",
            new { deviceId, at, userId, currentHours, sincePrevious, activeAlarmId, memo }, tx);

        await conn.ExecuteAsync(
            "UPDATE device_chiller SET maintenance_baseline_hours = @currentHours, maintenance_baseline_at = @at WHERE id = @deviceId",
            new { deviceId, currentHours, at }, tx);

        await conn.ExecuteAsync(
            "UPDATE alarm_event SET ended_at = @at, ack_by = @userId, ack_at = @at, memo = COALESCE(@memo, memo) " +
            "WHERE device_type = @deviceType AND device_id = @deviceId AND rule_code = @ruleCode AND ended_at IS NULL",
            new { deviceType = (int)AlarmDeviceType.Chiller, deviceId, ruleCode = MaintenanceRuleCode, at, userId, memo = memo ?? "保養完成" }, tx);

        await tx.CommitAsync(ct);
        return new ChillerMaintenanceResetResult(sincePrevious, activeAlarmId);
    }

    public async Task CloseActiveAlarmAsync(int deviceId, int userId, DateTimeOffset nowUtc, string memo, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var at = nowUtc.UtcDateTime;
        await conn.ExecuteAsync(
            "UPDATE alarm_event SET ended_at = @at, ack_by = @userId, ack_at = @at, memo = COALESCE(memo, @memo) " +
            "WHERE device_type = @deviceType AND device_id = @deviceId AND rule_code = @ruleCode AND ended_at IS NULL",
            new { deviceType = (int)AlarmDeviceType.Chiller, deviceId, ruleCode = MaintenanceRuleCode, at, userId, memo });
    }

    public async Task<IReadOnlyList<ChillerMaintenanceLogRow>> ListLogsAsync(int deviceId, int limit, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<ChillerMaintenanceLogRow>(
            "SELECT l.id, l.performed_at AS PerformedAt, l.performed_by AS PerformedBy, u.display_name AS PerformedByName, " +
            "l.hours_at_reset AS HoursAtReset, l.hours_since_previous AS HoursSincePrevious, l.memo " +
            "FROM chiller_maintenance_log l LEFT JOIN app_user u ON u.id = l.performed_by " +
            "WHERE l.device_id = @deviceId ORDER BY l.performed_at DESC LIMIT @limit",
            new { deviceId, limit });
        return rows.ToList();
    }
}
