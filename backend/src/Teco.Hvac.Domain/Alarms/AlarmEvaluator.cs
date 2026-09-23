using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Domain.Alarms;

/// <summary>單次門檻評估的輸入與結果。</summary>
public readonly record struct AlarmCheckResult(bool IsTriggered, double Value);

/// <summary>
/// 純函式的門檻評估器。刻意不依賴 DB/時間狀態機——debounce 由呼叫端（Infrastructure 層的
/// AlarmStateTracker）用 AlarmEvent 的 StartedAtUtc/EndedAtUtc 自行管理，這裡只回答
/// 「這個數值有沒有違反規則」。
/// </summary>
public static class AlarmEvaluator
{
    public static AlarmCheckResult Evaluate(AlarmRule rule, double value)
    {
        bool triggered = rule.Operator switch
        {
            AlarmMetricOperator.GreaterThan => value > rule.Threshold,
            AlarmMetricOperator.LessThan => value < rule.Threshold,
            AlarmMetricOperator.Equals => Math.Abs(value - rule.Threshold) < 0.0001,
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
        return new AlarmCheckResult(triggered, value);
    }
}
