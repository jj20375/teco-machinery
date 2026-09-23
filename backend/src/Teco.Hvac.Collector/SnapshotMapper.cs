using CS_NS_Communication_Golf_DDC;
using CS_NS_Communication_Modbus;
using CS_NS_Teco_Golf_DataCollector;
using Contracts = Teco.Hvac.Contracts;

namespace Teco.Hvac.Collector;

/// <summary>
/// 把供應商 DLL 的型別轉成 Teco.Hvac.Contracts 的 DTO，讓其餘專案不需要直接依賴
/// 供應商組件（隔離風險：日後若換供應商或介面改版，只有這個檔案要改）。
/// </summary>
internal static class SnapshotMapper
{
    public static Contracts.ChillerSnapshot ToContract(this TecoGolfHanbellStatus s, int modbusId, DateTimeOffset updateTimeUtc) =>
        new()
        {
            ModbusId = modbusId,
            UpdateTimeUtc = updateTimeUtc,
            ReadStatus = (Contracts.ReadStatus)s.ReadStatus,
            IsConnected = s.IsConnected,
            CoolingWaterOutletTemperature = s.CoolingWaterOutletTemperature,
            CoolingWaterInletTemperature = s.CoolingWaterInletTemperature,
            ChilledWaterOutletTemperature = s.ChilledWaterOutletTemperature,
            ChilledWaterTemperatureDifference = s.ChilledWaterTemperatureDifference,
            ChilledWaterInletTemperature = s.ChilledWaterInletTemperature,
            InputCurrent = s.InputCurrent,
            InputVoltage = s.InputVoltage,
            HighPressure = s.HighPressure,
            LowPressure = s.LowPressure,
            ActualRpm = s.ActualRpm,
            AccumulatedRunningHours = s.AccumulatedRunningHours,
            AccumulatedStartCount = s.AccumulatedStartCount,
            InputPowerKilowatt = s.InputPowerKilowatt,
            ApproachTemperature = s.ApproachTemperature,
            AccumulatedEnergyKilowattHour = s.AccumulatedEnergyKilowattHour,
            LoadPercentage = s.LoadPercentage,
            WaterControl = (Contracts.WaterControl)s.WaterControl,
            IsAlarm = s.IsAlarm,
            IsChilledWaterFlowAbnormal = s.IsChilledWaterFlowAbnormal,
            IsCoolingWaterFlowAbnormal = s.IsCoolingWaterFlowAbnormal,
            IsInverterAbnormal = s.IsInverterAbnormal,
            IsCompressorOverload = s.IsCompressorOverload,
            IsBearingTemperatureTooHigh = s.IsBearingTemperatureTooHigh,
            IsDischargeTemperatureTooHigh = s.IsDischargeTemperatureTooHigh,
            IsMotorTemperatureTooHigh = s.IsMotorTemperatureTooHigh,
            IsPowerVoltageTooHigh = s.IsPowerVoltageTooHigh,
            IsPowerVoltageTooLow = s.IsPowerVoltageTooLow,
            IsCurrentTooHigh = s.IsCurrentTooHigh,
            IsHighPressureTooHigh = s.IsHighPressureTooHigh,
            IsLowPressureTooLow = s.IsLowPressureTooLow,
            IsOutletAntiFreezeAbnormal = s.IsOutletAntiFreezeAbnormal,
            IsInletAntiFreezeAbnormal = s.IsInletAntiFreezeAbnormal,
        };

    public static Contracts.DdcSnapshot ToContract(this TecoGolfDDCStatus s, Contracts.Channel channel, DateTimeOffset updateTimeUtc) =>
        new()
        {
            Channel = channel,
            UpdateTimeUtc = updateTimeUtc,
            ReadStatus = (Contracts.ReadStatus)s.ReadStatus,
            IsConnected = s.IsConnected,
            FcuList = s.FCUList.Select(u => u.ToContract(channel)).ToList(),
        };

    private static Contracts.FcuSnapshot ToContract(this FCUUnit u, Contracts.Channel channel) =>
        new()
        {
            Channel = channel,
            StationId = u.StationID,
            Position = u.Position,
            Id = u.ID,
            Address = u.Address,
            SwitchStatus = (Contracts.FcuSwitchStatus)u.Value.SwitchStatus,
            Mode = (Contracts.FcuOperationMode)u.Value.Mode,
            FanSpeed = (Contracts.FcuFanSpeed)u.Value.FanSpeed,
            Temperature = u.Value.Temperature,
        };

    public static Contracts.Channel ToContractChannel(this EnumTecoGolfConnectionChannel c) => c switch
    {
        EnumTecoGolfConnectionChannel.HanbellModbusGateway => Contracts.Channel.HanbellModbusGateway,
        EnumTecoGolfConnectionChannel.DDC1 => Contracts.Channel.Ddc1,
        EnumTecoGolfConnectionChannel.DDC2 => Contracts.Channel.Ddc2,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static Contracts.ConnectionState ToContract(this EnumModbusConnectionState s) => (Contracts.ConnectionState)s;
}
