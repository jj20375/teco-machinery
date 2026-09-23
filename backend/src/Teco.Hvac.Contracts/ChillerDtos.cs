namespace Teco.Hvac.Contracts;

/// <summary>單台漢鐘冰水主機的完整快照，欄位對齊說明書表 12–14。</summary>
public sealed class ChillerSnapshot
{
    public required int ModbusId { get; init; }
    public required DateTimeOffset UpdateTimeUtc { get; init; }
    public required ReadStatus ReadStatus { get; init; }
    public required bool IsConnected { get; init; }

    public double CoolingWaterOutletTemperature { get; init; }
    public double CoolingWaterInletTemperature { get; init; }
    public double ChilledWaterOutletTemperature { get; init; }
    public double ChilledWaterTemperatureDifference { get; init; }
    public double ChilledWaterInletTemperature { get; init; }
    public double InputCurrent { get; init; }
    public double InputVoltage { get; init; }
    /// <summary>原始值已除以 100；物理單位未於公開模型定義，待供應商確認。</summary>
    public double HighPressure { get; init; }
    /// <summary>原始值已除以 100；物理單位未於公開模型定義，待供應商確認。</summary>
    public double LowPressure { get; init; }
    public int ActualRpm { get; init; }
    public int AccumulatedRunningHours { get; init; }
    public int AccumulatedStartCount { get; init; }
    public double InputPowerKilowatt { get; init; }
    public double ApproachTemperature { get; init; }
    public double AccumulatedEnergyKilowattHour { get; init; }
    public int LoadPercentage { get; init; }
    public WaterControl WaterControl { get; init; }

    public bool IsAlarm { get; init; }
    public bool IsChilledWaterFlowAbnormal { get; init; }
    public bool IsCoolingWaterFlowAbnormal { get; init; }
    public bool IsInverterAbnormal { get; init; }
    public bool IsCompressorOverload { get; init; }
    public bool IsBearingTemperatureTooHigh { get; init; }
    public bool IsDischargeTemperatureTooHigh { get; init; }
    public bool IsMotorTemperatureTooHigh { get; init; }
    public bool IsPowerVoltageTooHigh { get; init; }
    public bool IsPowerVoltageTooLow { get; init; }
    public bool IsCurrentTooHigh { get; init; }
    public bool IsHighPressureTooHigh { get; init; }
    public bool IsLowPressureTooLow { get; init; }
    public bool IsOutletAntiFreezeAbnormal { get; init; }
    public bool IsInletAntiFreezeAbnormal { get; init; }

    /// <summary>將 14 個警報旗標壓成 bitmask，供資料庫欄位 alarm_bits 使用。</summary>
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
