namespace Teco.Hvac.Domain.Entities;

public enum AlarmDeviceType { Chiller, Fcu }
public enum AlarmMetricOperator { GreaterThan, LessThan, Equals }

/// <summary>
/// 告警門檻規則。FCU 用「絕對室溫上下限」（決策：Collector 未提供 setpoint，
/// 見 docs/BACKEND_INTEGRATION_PLAN.md §2.1）；欄位保留 Scope 以支援日後改為 ΔT 規則。
/// </summary>
public sealed class AlarmRule
{
    public int Id { get; set; }
    public required AlarmDeviceType DeviceType { get; set; }
    /// <summary>套用範圍：FCU 為樓層或分區代碼；Chiller 為 ModbusId 或 "*"（全部）。</summary>
    public required string Scope { get; set; }
    public required string Metric { get; set; }
    public required AlarmMetricOperator Operator { get; set; }
    public required double Threshold { get; set; }
    public required Contracts.AlarmSeverity Severity { get; set; }
    /// <summary>去抖動秒數：超過門檻要持續這麼久才觸發，避免瞬間雜訊誤報。</summary>
    public int DebounceSeconds { get; set; } = 30;
    public bool IsEnabled { get; set; } = true;
}
