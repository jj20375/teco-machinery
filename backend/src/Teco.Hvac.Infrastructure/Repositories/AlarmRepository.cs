using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

public sealed class AlarmRepository(TecoDbConnectionFactory factory)
{
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

    /// <summary>單一設備+範圍底下目前設定的全部規則——給「告警門檻設定」畫面讀取用，不篩 is_enabled。</summary>
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
    /// 新增或覆寫一條規則。天然鍵是 (device_type, scope, metric, operator)——同一個設備對同一個
    /// 指標的同一個方向（大於/小於）只會有一條規則，改門檻值就是覆寫這一條，不是疊加新的一條。
    /// </summary>
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

    /// <summary>畫面把某個門檻欄位清空時呼叫——直接刪掉那一條規則，而不是留著一條停用的。</summary>
    public async Task DeleteRuleAsync(
        AlarmDeviceType deviceType, string scope, string metric, AlarmMetricOperator op, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM alarm_rule WHERE device_type = @deviceType AND scope = @scope AND metric = @metric AND operator = @op",
            new { deviceType = (int)deviceType, scope, metric, op = (int)op });
    }

    /// <summary>
    /// `fromUtc`/`toUtc` 給告警歷史報表用——依 `started_at` 篩區間，不受 `activeOnly` 影響
    /// （報表要看的是「這段期間發生過的事件」，不管現在是否還在告警中）。
    /// </summary>
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

    public async Task CloseEventAsync(int id, DateTimeOffset endedAtUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE alarm_event SET ended_at = @endedAt WHERE id = @id",
            new { id, endedAt = endedAtUtc.UtcDateTime });
    }

    public async Task<bool> TryFindActiveAsync(AlarmDeviceType deviceType, int deviceId, string ruleCode, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM alarm_event WHERE device_type=@deviceType AND device_id=@deviceId " +
            "AND rule_code=@ruleCode AND ended_at IS NULL",
            new { deviceType = (int)deviceType, deviceId, ruleCode });
        return count > 0;
    }

    public async Task AckAsync(long id, int userId, DateTimeOffset ackAtUtc, string? memo, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE alarm_event SET ack_by = @userId, ack_at = @ackAt, memo = COALESCE(@memo, memo) WHERE id = @id",
            new { id, userId, ackAt = ackAtUtc.UtcDateTime, memo });
    }

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
