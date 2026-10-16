using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Domain.Alarms;

/// <summary>
/// 單次告警門檻比對評估的輸入與判定結果結構 (<see cref="AlarmCheckResult"/>)。
/// </summary>
/// <param name="IsTriggered">該量測數值是否已違反規則定義之門檻條件（true 代表違規超標）。</param>
/// <param name="Value">參與比對評估的實際物理量測數值。</param>
public readonly record struct AlarmCheckResult(bool IsTriggered, double Value);

/// <summary>
/// 純函式領域告警門檻評估器 (<see cref="AlarmEvaluator"/>)。
/// 
/// 【設計理念】
/// 本類別為純粹的領域邏輯運算，不依賴任何資料庫、時間狀態機或外在環境；
/// 時間連續性與防抖動 (Debounce) 由呼叫端（如 AlarmEngine）自行管理，本類別僅專注於回答：
/// 「給定的數值是否違反此規則的運算子與門檻值」。
/// </summary>
public static class AlarmEvaluator
{
    /// <summary>
    /// 依據指定的告警規則運算子與門檻值，評估量測數值是否觸發告警條件。
    /// </summary>
    /// <param name="rule">告警門檻規則實體（包含比較運算子與門檻數值）。</param>
    /// <param name="value">被評估的當前物理量測數值（如溫度、電流等）。</param>
    /// <returns>包含是否違規與評估數值的 <see cref="AlarmCheckResult"/> 結構。</returns>
    /// <exception cref="ArgumentOutOfRangeException">當規則定義了不支援的比較運算子時拋出。</exception>
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
