using Dapper;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 冰水主機保養狀態物件 (<see cref="ChillerMaintenanceState"/>)。
/// </summary>
public sealed class ChillerMaintenanceState
{
    /// <summary>保養起算基準時數（上次保養時之累積運轉時數）；尚未初始化時為 null。</summary>
    public uint? BaselineHours { get; init; }

    /// <summary>基準時數設定或上次保養之時間；尚未初始化時為 null。</summary>
    public DateTime? BaselineAt { get; init; }
}

/// <summary>
/// 冰水主機保養履歷日誌 Row 物件 (<see cref="ChillerMaintenanceLogRow"/>)。
/// </summary>
public sealed class ChillerMaintenanceLogRow
{
    /// <summary>履歷紀錄主鍵 ID。</summary>
    public long Id { get; init; }

    /// <summary>保養執行登記之標準 UTC 時間。</summary>
    public DateTime PerformedAt { get; init; }

    /// <summary>執行保養登記之使用者 ID。</summary>
    public uint PerformedBy { get; init; }

    /// <summary>執行保養登記之使用者中文顯示名稱。</summary>
    public string? PerformedByName { get; init; }

    /// <summary>本次重置當下之主機累積運轉時數。</summary>
    public uint HoursAtReset { get; init; }

    /// <summary>距離前一次保養所運轉的時間長度（小時數）。</summary>
    public uint? HoursSincePrevious { get; init; }

    /// <summary>保養項目說明備註。</summary>
    public string? Memo { get; init; }
}

/// <summary>
/// 冰水主機保養完成重置作業之執行結果記錄 (<see cref="ChillerMaintenanceResetResult"/>)。
/// </summary>
/// <param name="HoursSincePrevious">距前一次保養累積運轉之小時數。</param>
/// <param name="ClosedAlarmEventId">因本次保養完成而一併自動解除結案的告警事件 ID。</param>
public sealed record ChillerMaintenanceResetResult(int? HoursSincePrevious, long? ClosedAlarmEventId);

/// <summary>
/// 冰水主機保養提醒與履歷資料庫倉儲 (<see cref="ChillerMaintenanceRepository"/>)。
/// 
/// 【設計模式：機車里程保養模式】
/// 記住上次保養完成時的累積運轉時數作為基準點 (BaselineHours)。
/// 當目前累積時數與基準點之差額達到設定之保養週期（例如 1000 小時）時開立保養通知告警；
/// 告警不會因數值微幅變動而自動熄滅，必須由值班人員於後台確認並點選「保養完成」後，
/// 於單一不可部分完成之資料庫交易內寫入保養履歷、更新基準點並解除告警。
/// </summary>
public sealed class ChillerMaintenanceRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 保養告警固定採用的規則代碼常數（不隨門檻數值動態改變代碼後綴，確保重置與結案精確對應）。
    /// </summary>
    public const string MaintenanceRuleCode = nameof(ChillerSnapshot.AccumulatedRunningHours);

    /// <summary>
    /// 查詢特定冰水主機當前的保養起算基準時數與設定時間。
    /// </summary>
    /// <param name="deviceId">冰水主機在資料庫中的主鍵 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>包含基準時數與設定時間的 <see cref="ChillerMaintenanceState"/> 物件。</returns>
    public async Task<ChillerMaintenanceState> GetStateAsync(int deviceId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ChillerMaintenanceState>(
            "SELECT maintenance_baseline_hours AS BaselineHours, maintenance_baseline_at AS BaselineAt " +
            "FROM device_chiller WHERE id = @deviceId", new { deviceId }) ?? new ChillerMaintenanceState();
    }

    /// <summary>
    /// 若主機尚未建立保養基準時數，以當前運轉時數作為初次起算點寫入資料庫。
    /// </summary>
    /// <param name="deviceId">冰水主機 ID。</param>
    /// <param name="hours">當前讀取到的運轉時數。</param>
    /// <param name="atUtc">設定時間戳記 (UTC)。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task InitBaselineIfMissingAsync(int deviceId, int hours, DateTimeOffset atUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE device_chiller SET maintenance_baseline_hours = @hours, maintenance_baseline_at = @at " +
            "WHERE id = @deviceId AND maintenance_baseline_hours IS NULL",
            new { deviceId, hours, at = atUtc.UtcDateTime });
    }

    /// <summary>
    /// 當主機控制器計時器歸零或更換控制板導致時數倒退時，重新校正起算基準點。
    /// </summary>
    /// <param name="deviceId">冰水主機 ID。</param>
    /// <param name="expectedBaseline">先前記錄的舊基準時數（作為樂觀鎖比對）。</param>
    /// <param name="hours">新的起算基準時數。</param>
    /// <param name="atUtc">重新校正的標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task RebaseAsync(int deviceId, uint expectedBaseline, int hours, DateTimeOffset atUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE device_chiller SET maintenance_baseline_hours = @hours, maintenance_baseline_at = @at " +
            "WHERE id = @deviceId AND maintenance_baseline_hours = @expectedBaseline",
            new { deviceId, expectedBaseline, hours, at = atUtc.UtcDateTime });
    }

    /// <summary>
    /// 依據運轉時數差額條件性開立保養告警。
    /// 透過單條 INSERT...SELECT 語法確保僅在「基準點未被他人修改」且「目前尚無未結案保養告警」時才新增告警，徹底杜絕競態條件。
    /// </summary>
    /// <param name="deviceId">冰水主機 ID。</param>
    /// <param name="expectedBaseline">期望比對的基準時數。</param>
    /// <param name="hoursSinceService">自上次保養以來已運轉之小時數。</param>
    /// <param name="nowUtc">當前計算時間戳記 (UTC)。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>若成功開立告警則回傳 true，否則回傳 false。</returns>
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

    /// <summary>
    /// 查詢時序歷史資料表中該主機最後一筆讀取成功的累積運轉時數（供主機離線時人工保養登記參考）。
    /// </summary>
    /// <param name="deviceId">冰水主機 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>最後記錄之運轉時數；若無資料則為 null。</returns>
    public async Task<int?> GetLatestRecordedHoursAsync(int deviceId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(
            "SELECT running_hours FROM chiller_reading WHERE device_id = @deviceId AND read_status = 1 " +
            "AND running_hours IS NOT NULL ORDER BY ts DESC LIMIT 1", new { deviceId });
    }

    /// <summary>
    /// 執行「保養完成」重置事務：
    /// 在單一資料庫交易內鎖定並讀取基準點、計算歷時、寫入保養履歷、更新主機基準點為當前時數，並將進行中的保養告警解除結案。
    /// </summary>
    /// <param name="deviceId">冰水主機 ID。</param>
    /// <param name="currentHours">本次保養當下的累積運轉時數。</param>
    /// <param name="userId">執行確認登記的管理員 ID。</param>
    /// <param name="nowUtc">作業執行時間戳記 (UTC)。</param>
    /// <param name="memo">保養內容備註說明。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>包含距離上次保養時數與結案告警 ID 的重置結果。</returns>
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

    /// <summary>
    /// 手動關閉解除進行中的主機保養告警並補填備註。
    /// </summary>
    /// <param name="deviceId">冰水主機 ID。</param>
    /// <param name="userId">執行操作之管理員 ID。</param>
    /// <param name="nowUtc">解除時間戳記 (UTC)。</param>
    /// <param name="memo">解除說明備註。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task CloseActiveAlarmAsync(int deviceId, int userId, DateTimeOffset nowUtc, string memo, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var at = nowUtc.UtcDateTime;
        await conn.ExecuteAsync(
            "UPDATE alarm_event SET ended_at = @at, ack_by = @userId, ack_at = @at, memo = COALESCE(memo, @memo) " +
            "WHERE device_type = @deviceType AND device_id = @deviceId AND rule_code = @ruleCode AND ended_at IS NULL",
            new { deviceType = (int)AlarmDeviceType.Chiller, deviceId, ruleCode = MaintenanceRuleCode, at, userId, memo });
    }

    /// <summary>
    /// 依據冰水主機 ID 查詢其歷史保養紀錄履歷清單。
    /// </summary>
    /// <param name="deviceId">冰水主機 ID。</param>
    /// <param name="limit">最大查詢筆數。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>保養履歷 Row 物件清單。</returns>
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
