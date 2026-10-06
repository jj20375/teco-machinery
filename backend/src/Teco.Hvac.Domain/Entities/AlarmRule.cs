using System.Globalization;

namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// Channel 不是實體設備而是一條通訊通道（device_id＝Contracts.Channel 的數值：0 漢鐘 Gateway、1 DDC1、2 DDC2），
/// 給「離線」告警用：DDC 讀取失敗時整層 FCU 都會離線，逐台開告警會一次冒出 64 筆。
/// </summary>
public enum AlarmDeviceType { Chiller, Fcu, Channel }
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

    /// <summary>
    /// 寫進 alarm_event.rule_code 的識別字串。含門檻值，所以改門檻後舊告警的代碼會對不上新規則，
    /// Collector 永遠不會再評估、也不會關掉它——改門檻的一方要自己收掉（見 AlarmRepository.CloseStaleThresholdEventsAsync）。
    /// </summary>
    public string RuleCode => BuildRuleCode(Metric, Operator, Threshold);

    public static string BuildRuleCode(string metric, AlarmMetricOperator op, double threshold) =>
        $"{metric}.{op}.{threshold.ToString(CultureInfo.InvariantCulture)}";

    public static string RuleCodePrefix(string metric, AlarmMetricOperator op) => $"{metric}.{op}.";
}
