using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 告警規則與事件資料庫倉儲 (<see cref="AlarmRepository"/>)。
/// 
/// 負責 alarm_rule 與 alarm_event 資料表之存取操作。
/// 支援告警規則讀取、快取指紋計算、規則的新增覆寫與刪除、告警事件的開立、解除、人工確認與報表分頁查詢。
/// </summary>
public sealed class AlarmRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 查詢所有處於啟用狀態 (is_enabled = 1) 的告警規則清單。
    /// 供採集核心 AlarmEngine 於啟動或規則變更時載入記憶體快取。
    /// </summary>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>啟用的告警規則實體集合。</returns>
    public async Task<IReadOnlyList<AlarmRule>> GetEnabledRulesAsync(CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<AlarmRuleRow>(
            "SELECT id, device_type AS DeviceType, scope, metric, operator AS Operator, threshold, " +
            "severity, debounce_seconds AS DebounceSeconds, is_enabled AS IsEnabled " +
            "FROM alarm_rule WHERE is_enabled = 1");
        return rows.Select(r => new AlarmRule
        {
            Id = r.Id, DeviceType = (AlarmDeviceType)r.DeviceType, Scope = r.Scope, Metric = r.Metric,
            Operator = (AlarmMetricOperator)r.Operator, Threshold = r.Threshold,
            Severity = (Contracts.AlarmSeverity)r.Severity, DebounceSeconds = r.DebounceSeconds, IsEnabled = r.IsEnabled,
        }).ToList();
    }

    /// <summary>
    /// 計算全部告警規則（包含啟用與停用）的綜合校驗指紋字串（由總筆數加上 CRC32 校驗碼總和組成）。
    /// Collector 每次評估前快速讀取此指紋比對，若後台有任何修改（門檻、開關、增刪）即可即刻重載，無需等待定時輪詢。
    /// </summary>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表規則最新狀態的指紋字串。</returns>
    public async Task<string> GetRulesFingerprintAsync(CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteScalarAsync<string>(
            "SELECT CONCAT(COUNT(*), ':', COALESCE(SUM(CRC32(CONCAT_WS('|', id, device_type, scope, metric, operator, " +
            "threshold, severity, debounce_seconds, is_enabled))), 0)) FROM alarm_rule") ?? "";
    }

    /// <summary>
    /// 查詢特定設備類型與套用範圍底下的全部規則（不篩選 is_enabled）。
    /// 供前台「告警門檻設定」管理視窗回顯目前設定值使用。
    /// </summary>
    /// <param name="deviceType">目標設備類型（Chiller 或 Fcu）。</param>
    /// <param name="scope">套用範圍（樓層或 ModbusId）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>該範圍下配置的所有規則實體集合。</returns>
    public async Task<IReadOnlyList<AlarmRule>> GetRulesAsync(AlarmDeviceType deviceType, string scope, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<AlarmRuleRow>(
            "SELECT id, device_type AS DeviceType, scope, metric, operator AS Operator, threshold, " +
            "severity, debounce_seconds AS DebounceSeconds, is_enabled AS IsEnabled " +
            "FROM alarm_rule WHERE device_type = @deviceType AND scope = @scope",
            new { deviceType = (int)deviceType, scope });
        return rows.Select(r => new AlarmRule
        {
            Id = r.Id, DeviceType = (AlarmDeviceType)r.DeviceType, Scope = r.Scope, Metric = r.Metric,
            Operator = (AlarmMetricOperator)r.Operator, Threshold = r.Threshold,
            Severity = (Contracts.AlarmSeverity)r.Severity, DebounceSeconds = r.DebounceSeconds, IsEnabled = r.IsEnabled,
        }).ToList();
    }

    /// <summary>
    /// 新增或覆寫更新單條告警規則。
    /// 依據 (device_type, scope, metric, operator) 複合唯一鍵，若已存在則覆寫門檻、嚴重度與防抖動時間。
    /// </summary>
    /// <param name="rule">欲寫入或更新的規則物件。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task UpsertRuleAsync(AlarmRule rule, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            """
            INSERT INTO alarm_rule (device_type, scope, metric, operator, threshold, severity, debounce_seconds, is_enabled)
            VALUES (@DeviceType, @Scope, @Metric, @Operator, @Threshold, @Severity, @DebounceSeconds, @IsEnabled)
            ON DUPLICATE KEY UPDATE threshold = @Threshold, severity = @Severity,
                debounce_seconds = @DebounceSeconds, is_enabled = @IsEnabled
            """, new
            {
                DeviceType = (int)rule.DeviceType, rule.Scope, rule.Metric, Operator = (int)rule.Operator,
                rule.Threshold, Severity = (int)rule.Severity, rule.DebounceSeconds, rule.IsEnabled,
            });
    }

    /// <summary>
    /// 刪除指定條件之告警門檻規則。通常用於管理員於前台清空某門檻欄位時調用。
    /// </summary>
    /// <param name="deviceType">設備類型。</param>
    /// <param name="scope">套用範圍。</param>
    /// <param name="metric">量測指標名稱。</param>
    /// <param name="op">比較運算子。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步刪除作業的 <see cref="Task"/>。</returns>
    public async Task DeleteRuleAsync(
        AlarmDeviceType deviceType, string scope, string metric, AlarmMetricOperator op, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM alarm_rule WHERE device_type = @deviceType AND scope = @scope AND metric = @metric AND operator = @op",
            new { deviceType = (int)deviceType, scope, metric, op = (int)op });
    }

    /// <summary>
    /// 查詢告警事件清單。支援篩選僅查詢未結案事件、時間區間篩選以及筆數上限限制。
    /// 供即時告警清單與歷史告警報表共用。
    /// </summary>
    /// <param name="activeOnly">是否僅查詢進行中（未結案）的告警事件。</param>
    /// <param name="limit">最大回傳筆數。</param>
    /// <param name="fromUtc">起算時間（UTC）；若未指定則不限起始時間。</param>
    /// <param name="toUtc">截止時間（UTC）；若未指定則不限結束時間。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>符合條件之告警事件實體清單。</returns>
    public async Task<IReadOnlyList<AlarmEvent>> ListAsync(
        bool activeOnly, int limit, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var sql = "SELECT id, device_type AS DeviceType, device_id AS DeviceId, rule_code AS RuleCode, " +
                   "severity, started_at AS StartedAt, ended_at AS EndedAt, peak_value AS PeakValue, " +
                   "ack_by AS AckByUserId, ack_at AS AckAt, memo FROM alarm_event WHERE 1=1";
        if (activeOnly) sql += " AND ended_at IS NULL";
        if (fromUtc is not null) sql += " AND started_at >= @fromUtc";
        if (toUtc is not null) sql += " AND started_at <= @toUtc";
        sql += " ORDER BY started_at DESC LIMIT @limit";

        var rows = await conn.QueryAsync<AlarmEventRow>(sql, new { limit, fromUtc, toUtc });
        return rows.Select(r => new AlarmEvent
        {
            Id = r.Id, DeviceType = (AlarmDeviceType)r.DeviceType, DeviceId = r.DeviceId, RuleCode = r.RuleCode,
            Severity = (Contracts.AlarmSeverity)r.Severity, StartedAtUtc = r.StartedAt.AsUtcOffset(), EndedAtUtc = r.EndedAt.AsUtcOffset(),
            PeakValue = r.PeakValue, AckByUserId = r.AckByUserId, AckAtUtc = r.AckAt.AsUtcOffset(), Memo = r.Memo,
        }).ToList();
    }

    /// <summary>
    /// 新增開立一筆全新的告警事件。
    /// </summary>
    /// <param name="deviceType">發生告警之設備類型。</param>
    /// <param name="deviceId">設備主鍵 ID（或通訊通道數值）。</param>
    /// <param name="ruleCode">觸發告警之標準規則代碼。</param>
    /// <param name="severity">告警嚴重等級。</param>
    /// <param name="startedAtUtc">告警觸發開立之標準 UTC 時間戳記。</param>
    /// <param name="peakValue">觸發時之極端物理量數值。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>新增告警事件在資料庫中的自動遞增主鍵 ID。</returns>
    public async Task<int> OpenEventAsync(AlarmDeviceType deviceType, int deviceId, string ruleCode,
        Contracts.AlarmSeverity severity, DateTimeOffset startedAtUtc, double peakValue, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO alarm_event (device_type, device_id, rule_code, severity, started_at, peak_value) " +
            "VALUES (@deviceType, @deviceId, @ruleCode, @severity, @startedAt, @peakValue); SELECT LAST_INSERT_ID();",
            new { deviceType = (int)deviceType, deviceId, ruleCode, severity = (int)severity, startedAt = startedAtUtc.UtcDateTime, peakValue });
        return (int)id;
    }

    /// <summary>
    /// 依據告警事件主鍵 ID，將該告警標記為已結案解除。
    /// </summary>
    /// <param name="id">告警事件 ID。</param>
    /// <param name="endedAtUtc">告警解除之標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步結案作業的 <see cref="Task"/>。</returns>
    public async Task CloseEventAsync(int id, DateTimeOffset endedAtUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE alarm_event SET ended_at = @endedAt WHERE id = @id",
            new { id, endedAt = endedAtUtc.UtcDateTime });
    }

    /// <summary>
    /// 當管理員修改或清空門檻後，關閉具有相同指標與比較方向、但門檻已過時 (非 keepRuleCode) 的未結案告警。
    /// </summary>
    /// <param name="deviceType">設備類型。</param>
    /// <param name="deviceId">特定設備 ID；若為 null 則套用於該類型下全部設備。</param>
    /// <param name="metric">指標名稱。</param>
    /// <param name="op">運算子。</param>
    /// <param name="keepRuleCode">新門檻之規則代碼；若為 null 則代表門檻已被刪除，全數結案。</param>
    /// <param name="endedAtUtc">結案時間戳記。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>被更新結案的告警事件筆數。</returns>
    public async Task<int> CloseStaleThresholdEventsAsync(
        AlarmDeviceType deviceType, int? deviceId, string metric, AlarmMetricOperator op, string? keepRuleCode,
        DateTimeOffset endedAtUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteAsync(
            """
            UPDATE alarm_event SET ended_at = @endedAt
            WHERE ended_at IS NULL AND device_type = @deviceType
              AND (@deviceId IS NULL OR device_id = @deviceId)
              AND LEFT(rule_code, CHAR_LENGTH(@prefix)) = @prefix
              AND (@keepRuleCode IS NULL OR rule_code <> @keepRuleCode)
            """,
            new
            {
                deviceType = (int)deviceType, deviceId, prefix = AlarmRule.RuleCodePrefix(metric, op), keepRuleCode,
                endedAt = endedAtUtc.UtcDateTime,
            });
    }

    /// <summary>
    /// 關閉所有代碼不屬於任何現行啟用規則的孤兒門檻類告警事件。
    /// 供 Collector 每次載入規則後執行全面掃描清理。
    /// </summary>
    /// <param name="validRuleCodes">目前有效啟用的規則代碼集合。</param>
    /// <param name="endedAtUtc">結案時間戳記。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>被關閉結案的孤兒告警筆數。</returns>
    public async Task<int> CloseOrphanThresholdEventsAsync(
        IReadOnlyCollection<string> validRuleCodes, DateTimeOffset endedAtUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteAsync(
            """
            UPDATE alarm_event SET ended_at = @endedAt
            WHERE ended_at IS NULL
              AND (rule_code LIKE '%.GreaterThan.%' OR rule_code LIKE '%.LessThan.%' OR rule_code LIKE '%.Equals.%')
              AND rule_code NOT IN @validRuleCodes
            """,
            new { endedAt = endedAtUtc.UtcDateTime, validRuleCodes = validRuleCodes.Count == 0 ? new[] { "" } : validRuleCodes.ToArray() });
    }

    /// <summary>
    /// 依據設備類型、設備 ID 與規則代碼，結案正在進行中的告警事件。
    /// </summary>
    /// <param name="deviceType">設備類型。</param>
    /// <param name="deviceId">設備 ID。</param>
    /// <param name="ruleCode">規則代碼。</param>
    /// <param name="endedAtUtc">結案時間戳記。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>被結案的告警筆數。</returns>
    public async Task<int> CloseActiveEventsAsync(
        AlarmDeviceType deviceType, int deviceId, string ruleCode, DateTimeOffset endedAtUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteAsync(
            "UPDATE alarm_event SET ended_at = @endedAt " +
            "WHERE device_type = @deviceType AND device_id = @deviceId AND rule_code = @ruleCode AND ended_at IS NULL",
            new { deviceType = (int)deviceType, deviceId, ruleCode, endedAt = endedAtUtc.UtcDateTime });
    }

    /// <summary>
    /// 一次結案同一台設備多個規則代碼的未結案告警（設備停機／關機時結案溫度門檻告警用）。
    /// </summary>
    /// <param name="deviceType">設備類型。</param>
    /// <param name="deviceId">設備 ID。</param>
    /// <param name="ruleCodes">要結案的規則代碼；空集合時不做任何事。</param>
    /// <param name="endedAtUtc">結案時間戳記。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>被結案的告警筆數。</returns>
    public async Task<int> CloseActiveEventsAsync(
        AlarmDeviceType deviceType, int deviceId, IReadOnlyCollection<string> ruleCodes, DateTimeOffset endedAtUtc,
        CancellationToken ct = default)
    {
        if (ruleCodes.Count == 0) return 0;
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteAsync(
            "UPDATE alarm_event SET ended_at = @endedAt " +
            "WHERE device_type = @deviceType AND device_id = @deviceId AND rule_code IN @ruleCodes AND ended_at IS NULL",
            new { deviceType = (int)deviceType, deviceId, ruleCodes = ruleCodes.ToArray(), endedAt = endedAtUtc.UtcDateTime });
    }

    /// <summary>
    /// 檢查特定設備的特定規則代碼當前是否已有未結案 (Active) 的告警事件。
    /// </summary>
    /// <param name="deviceType">設備類型。</param>
    /// <param name="deviceId">設備 ID。</param>
    /// <param name="ruleCode">規則代碼。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>若已存在未結案告警則回傳 true，否則回傳 false。</returns>
    public async Task<bool> TryFindActiveAsync(AlarmDeviceType deviceType, int deviceId, string ruleCode, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM alarm_event WHERE device_type=@deviceType AND device_id=@deviceId " +
            "AND rule_code=@ruleCode AND ended_at IS NULL",
            new { deviceType = (int)deviceType, deviceId, ruleCode });
        return count > 0;
    }

    /// <summary>
    /// 對指定的告警事件執行人工確認 (Acknowledge) 並寫入處理備註。
    /// </summary>
    /// <param name="id">告警事件主鍵 ID。</param>
    /// <param name="userId">執行確認動作之使用者 ID。</param>
    /// <param name="ackAtUtc">確認作業發生的標準 UTC 時間戳記。</param>
    /// <param name="memo">處理說明備註；若未輸入則保留原有備註。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步確認作業的 <see cref="Task"/>。</returns>
    public async Task AckAsync(long id, int userId, DateTimeOffset ackAtUtc, string? memo, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE alarm_event SET ack_by = @userId, ack_at = @ackAt, memo = COALESCE(@memo, memo) WHERE id = @id",
            new { id, userId, ackAt = ackAtUtc.UtcDateTime, memo });
    }

    /// <summary>Dapper 內部查詢 alarm_rule 資料表對應 Row 物件。</summary>
    private sealed class AlarmRuleRow
    {
        public int Id { get; init; }
        public int DeviceType { get; init; }
        public required string Scope { get; init; }
        public required string Metric { get; init; }
        public int Operator { get; init; }
        public double Threshold { get; init; }
        public int Severity { get; init; }
        public int DebounceSeconds { get; init; }
        public bool IsEnabled { get; init; }
    }

    /// <summary>Dapper 內部查詢 alarm_event 資料表對應 Row 物件。</summary>
    private sealed class AlarmEventRow
    {
        public long Id { get; init; }
        public int DeviceType { get; init; }
        public int DeviceId { get; init; }
        public required string RuleCode { get; init; }
        public int Severity { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? EndedAt { get; init; }
        public double? PeakValue { get; init; }
        public int? AckByUserId { get; init; }
        public DateTime? AckAt { get; init; }
        public string? Memo { get; init; }
    }
}
