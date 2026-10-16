using System.Globalization;

namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 告警適用之設備與通訊物件類型列舉 (<see cref="AlarmDeviceType"/>)。
/// </summary>
public enum AlarmDeviceType
{
    /// <summary>漢鐘冰水主機設備。</summary>
    Chiller,
    /// <summary>FCU 送風機設備。</summary>
    Fcu,
    /// <summary>
    /// 實體通訊通道（Channel 0 漢鐘閘道、Channel 1 DDC1、Channel 2 DDC2）。
    /// 用於集中通報「整條通道離線」，避免整層幾十台 FCU 同時噴出大量重複告警。
    /// </summary>
    Channel
}

/// <summary>
/// 告警門檻數值比較運算子列舉 (<see cref="AlarmMetricOperator"/>)。
/// </summary>
public enum AlarmMetricOperator
{
    /// <summary>大於門檻值 (>)</summary>
    GreaterThan,
    /// <summary>小於門檻值 (&lt;)</summary>
    LessThan,
    /// <summary>等於門檻值 (==)</summary>
    Equals
}

/// <summary>
/// 告警動態門檻規則實體 (<see cref="AlarmRule"/>)。
/// 對應資料庫 alarm_rule 資料表。
/// 支援使用者於後台依據樓層、分區或單機設定各項物理指標之告警門檻與防抖動秒數。
/// </summary>
public sealed class AlarmRule
{
    /// <summary>規則主鍵識別碼 (Primary Key)。</summary>
    public int Id { get; set; }

    /// <summary>規則適用之設備或通道類型。</summary>
    public required AlarmDeviceType DeviceType { get; set; }

    /// <summary>
    /// 規則套用範圍 (Scope)：
    /// - FCU：可設定為樓層（如 "B1"、"B2"）或特定分區代碼，"*" 代表全廠 FCU 通用。
    /// - Chiller：可設定為特定 ModbusId（如 "1" 或 "2"），"*" 代表全廠冰機通用。
    /// </summary>
    public required string Scope { get; set; }

    /// <summary>評估之物理指標名稱（例如 "Temperature"、"ChilledWaterOutletTemperature"、"AccumulatedRunningHours"）。</summary>
    public required string Metric { get; set; }

    /// <summary>比較運算子（大於、小於或等於）。</summary>
    public required AlarmMetricOperator Operator { get; set; }

    /// <summary>告警觸發之門檻數值。</summary>
    public required double Threshold { get; set; }

    /// <summary>達到門檻時開立之告警嚴重度等級（Info、Warning 或 Critical）。</summary>
    public required Contracts.AlarmSeverity Severity { get; set; }

    /// <summary>防抖動延遲秒數（Debounce）：量測數值必須連續超標維持達此秒數才正式開立告警，過濾瞬間突波與雜訊。</summary>
    public int DebounceSeconds { get; set; } = 30;

    /// <summary>此規則是否處於啟用狀態（false 代表略過評估）。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 產生寫入 alarm_event.rule_code 的標準規則代碼字串。
    /// 包含指標名稱、運算子與門檻值數值。
    /// </summary>
    public string RuleCode => BuildRuleCode(Metric, Operator, Threshold);

    /// <summary>
    /// 靜態輔助方法：依據指標、運算子與門檻值組合成標準 RuleCode 字串。
    /// </summary>
    /// <param name="metric">指標名稱。</param>
    /// <param name="op">比較運算子。</param>
    /// <param name="threshold">門檻數值。</param>
    /// <returns>格式如 "Metric.Operator.Threshold" 的標準代碼字串。</returns>
    public static string BuildRuleCode(string metric, AlarmMetricOperator op, double threshold) =>
        $"{metric}.{op}.{threshold.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// 靜態輔助方法：取得該指標與運算子之前綴字串，用於查詢或清理舊門檻之遺留告警。
    /// </summary>
    /// <param name="metric">指標名稱。</param>
    /// <param name="op">比較運算子。</param>
    /// <returns>格式如 "Metric.Operator." 的前綴字串。</returns>
    public static string RuleCodePrefix(string metric, AlarmMetricOperator op) => $"{metric}.{op}.";
}
