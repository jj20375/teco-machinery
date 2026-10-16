namespace Teco.Hvac.Contracts;

/// <summary>
/// 單台漢鐘冰水主機 (Hanbell Chiller) 的完整遙測快照物件。
/// 欄位定義完全對齊供應商原廠技術說明書表 12–14。
/// </summary>
public sealed class ChillerSnapshot
{
    /// <summary>冰水主機在 RS-485 Modbus 匯流排上的站號（1 或 2）。</summary>
    public required int ModbusId { get; init; }

    /// <summary>本次遙測數據在採集端記錄之標準 UTC 時間戳記。</summary>
    public required DateTimeOffset UpdateTimeUtc { get; init; }

    /// <summary>本次通訊輪詢之數據讀取狀態（成功、失敗或未讀）。</summary>
    public required ReadStatus ReadStatus { get; init; }

    /// <summary>主機是否處於通訊連線狀態。</summary>
    public required bool IsConnected { get; init; }

    /// <summary>冷卻水出水溫度（單位：°C）。</summary>
    public double CoolingWaterOutletTemperature { get; init; }

    /// <summary>冷卻水進水溫度（單位：°C）。</summary>
    public double CoolingWaterInletTemperature { get; init; }

    /// <summary>冰水出水溫度（供水溫度，單位：°C）。</summary>
    public double ChilledWaterOutletTemperature { get; init; }

    /// <summary>冰水進出水溫差（進水溫度 - 出水溫度，單位：°C）。</summary>
    public double ChilledWaterTemperatureDifference { get; init; }

    /// <summary>冰水進水溫度（回水溫度，單位：°C）。</summary>
    public double ChilledWaterInletTemperature { get; init; }

    /// <summary>主機運轉輸入電流（單位：A）。</summary>
    public double InputCurrent { get; init; }

    /// <summary>主機運轉輸入電壓（單位：V）。</summary>
    public double InputVoltage { get; init; }

    /// <summary>
    /// 冷媒高壓壓力。
    /// 原始暫存器數值已除以 100；物理單位待供應商進一步確認（通常為 bar 或 kg/cm²）。
    /// </summary>
    public double HighPressure { get; init; }

    /// <summary>
    /// 冷媒低壓壓力。
    /// 原始暫存器數值已除以 100；物理單位待供應商進一步確認（通常為 bar 或 kg/cm²）。
    /// </summary>
    public double LowPressure { get; init; }

    /// <summary>壓縮機目前實際轉速（單位：RPM）。</summary>
    public int ActualRpm { get; init; }

    /// <summary>主機累積運轉時數（單位：小時，Hour），供週期性保養提醒使用。</summary>
    public int AccumulatedRunningHours { get; init; }

    /// <summary>主機累計啟動次數。</summary>
    public int AccumulatedStartCount { get; init; }

    /// <summary>主機即時輸入功率（單位：kW）。</summary>
    public double InputPowerKilowatt { get; init; }

    /// <summary>趨近溫度 / 趨近度（Approach Temperature，單位：°C），評估熱交換器結垢之重要指標。</summary>
    public double ApproachTemperature { get; init; }

    /// <summary>主機累計耗電量（單位：kWh，度）。</summary>
    public double AccumulatedEnergyKilowattHour { get; init; }

    /// <summary>主機目前運轉負載百分比（0 ~ 100%）。</summary>
    public int LoadPercentage { get; init; }

    /// <summary>水流控制模式（進水控制或出水控制）。</summary>
    public WaterControl WaterControl { get; init; }

    /// <summary>主機總體警報旗標（true 代表有任一硬體故障觸發）。</summary>
    public bool IsAlarm { get; init; }

    /// <summary>冰水水流異常警報旗標。</summary>
    public bool IsChilledWaterFlowAbnormal { get; init; }

    /// <summary>冷卻水水流異常警報旗標。</summary>
    public bool IsCoolingWaterFlowAbnormal { get; init; }

    /// <summary>變頻器異常警報旗標。</summary>
    public bool IsInverterAbnormal { get; init; }

    /// <summary>壓縮機過載警報旗標。</summary>
    public bool IsCompressorOverload { get; init; }

    /// <summary>軸承溫度過高警報旗標。</summary>
    public bool IsBearingTemperatureTooHigh { get; init; }

    /// <summary>排氣溫度過高警報旗標。</summary>
    public bool IsDischargeTemperatureTooHigh { get; init; }

    /// <summary>馬達溫度過高警報旗標。</summary>
    public bool IsMotorTemperatureTooHigh { get; init; }

    /// <summary>電源電壓過高警報旗標。</summary>
    public bool IsPowerVoltageTooHigh { get; init; }

    /// <summary>電源電壓過低警報旗標。</summary>
    public bool IsPowerVoltageTooLow { get; init; }

    /// <summary>運轉電流過高警報旗標。</summary>
    public bool IsCurrentTooHigh { get; init; }

    /// <summary>冷媒高壓過高警報旗標。</summary>
    public bool IsHighPressureTooHigh { get; init; }

    /// <summary>冷媒低壓過低警報旗標。</summary>
    public bool IsLowPressureTooLow { get; init; }

    /// <summary>出水防凍異常警報旗標。</summary>
    public bool IsOutletAntiFreezeAbnormal { get; init; }

    /// <summary>進水防凍異常警報旗標。</summary>
    public bool IsInletAntiFreezeAbnormal { get; init; }

    /// <summary>
    /// 將 14 個硬體警報布林旗標依序壓縮為單一整數遮罩 (Bitmask)，
    /// 供資料庫時序資料表之 alarm_bits 欄位高效率儲存與快速查詢。
    /// </summary>
    /// <returns>代表 14 個故障旗標的二進位整數遮罩。</returns>
    public int ToAlarmBits() =>
        (IsChilledWaterFlowAbnormal ? 1 << 0 : 0) |
        (IsCoolingWaterFlowAbnormal ? 1 << 1 : 0) |
        (IsInverterAbnormal ? 1 << 2 : 0) |
        (IsCompressorOverload ? 1 << 3 : 0) |
        (IsBearingTemperatureTooHigh ? 1 << 4 : 0) |
        (IsDischargeTemperatureTooHigh ? 1 << 5 : 0) |
        (IsMotorTemperatureTooHigh ? 1 << 6 : 0) |
        (IsPowerVoltageTooHigh ? 1 << 7 : 0) |
        (IsPowerVoltageTooLow ? 1 << 8 : 0) |
        (IsCurrentTooHigh ? 1 << 9 : 0) |
        (IsHighPressureTooHigh ? 1 << 10 : 0) |
        (IsLowPressureTooLow ? 1 << 11 : 0) |
        (IsOutletAntiFreezeAbnormal ? 1 << 12 : 0) |
        (IsInletAntiFreezeAbnormal ? 1 << 13 : 0);
}
