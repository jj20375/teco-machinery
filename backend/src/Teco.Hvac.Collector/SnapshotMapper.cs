using CS_NS_Communication_Golf_DDC;
using CS_NS_Communication_Modbus;
using CS_NS_Teco_Golf_DataCollector;
using Contracts = Teco.Hvac.Contracts;

namespace Teco.Hvac.Collector;

/// <summary>
/// 供應商資料結構與系統合約 DTO 之轉換映射器 (SnapshotMapper)。
/// 
/// 負責將供應商 DLL (<see cref="CS_TecoGolf_DataCollector"/>) 所產生的專有型別轉換為 <see cref="Teco.Hvac.Contracts"/> 之領域 DTO。
/// 
/// 【架構隔離價值】
/// 透過本映射層，系統其餘模組（API、時序寫入、SignalR、前台看板）皆無需直接參考或相依於第三方供應商 DLL；
/// 未來若更換通訊協定或硬體供應商改版，僅需修改此映射檔案即可完成相容。
/// </summary>
internal static class SnapshotMapper
{
    /// <summary>
    /// 將供應商之漢鐘冰水主機狀態物件 (<see cref="TecoGolfHanbellStatus"/>) 轉換為系統合約快照物件 (<see cref="Contracts.ChillerSnapshot"/>)。
    /// </summary>
    /// <param name="s">供應商回傳之漢鐘冰水主機原始狀態物件。</param>
    /// <param name="modbusId">此冰水主機所屬之 Modbus 站號 (1 或 2)。</param>
    /// <param name="updateTimeUtc">本次採集數據之標準 UTC 時間戳記。</param>
    /// <returns>系統標準之冰水主機遙測快照物件。</returns>
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

    /// <summary>
    /// 將供應商之 DDC 控制器狀態物件 (<see cref="TecoGolfDDCStatus"/>) 轉換為系統合約快照物件 (<see cref="Contracts.DdcSnapshot"/>)。
    /// </summary>
    /// <param name="s">供應商回傳之 DDC 原始狀態物件。</param>
    /// <param name="channel">該 DDC 所屬之通訊通道（DDC1 或 DDC2）。</param>
    /// <param name="updateTimeUtc">本次採集數據之標準 UTC 時間戳記。</param>
    /// <returns>系統標準之 DDC 控制器遙測快照物件（包含所轄所有 FCU 清單）。</returns>
    public static Contracts.DdcSnapshot ToContract(this TecoGolfDDCStatus s, Contracts.Channel channel, DateTimeOffset updateTimeUtc) =>
        new()
        {
            Channel = channel,
            UpdateTimeUtc = updateTimeUtc,
            ReadStatus = (Contracts.ReadStatus)s.ReadStatus,
            IsConnected = s.IsConnected,
            FcuList = s.FCUList.Select(u => u.ToContract(channel)).ToList(),
        };

    /// <summary>
    /// 將供應商單台 FCU 實體物件 (<see cref="FCUUnit"/>) 轉換為系統合約之 FCU 快照物件 (<see cref="Contracts.FcuSnapshot"/>)。
    /// </summary>
    /// <param name="u">供應商回傳之單台 FCU 原始物件。</param>
    /// <param name="channel">該 FCU 所屬之通訊通道。</param>
    /// <returns>系統標準之 FCU 遙測快照物件。</returns>
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

    /// <summary>
    /// 將供應商連線通道列舉 (<see cref="EnumTecoGolfConnectionChannel"/>) 轉換為系統合約通道列舉 (<see cref="Contracts.Channel"/>)。
    /// </summary>
    /// <param name="c">供應商連線通道列舉值。</param>
    /// <returns>系統合約之通道列舉值。</returns>
    /// <exception cref="ArgumentOutOfRangeException">當傳入未知通道列舉值時拋出例外。</exception>
    public static Contracts.Channel ToContractChannel(this EnumTecoGolfConnectionChannel c) => c switch
    {
        EnumTecoGolfConnectionChannel.HanbellModbusGateway => Contracts.Channel.HanbellModbusGateway,
        EnumTecoGolfConnectionChannel.DDC1 => Contracts.Channel.Ddc1,
        EnumTecoGolfConnectionChannel.DDC2 => Contracts.Channel.Ddc2,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>
    /// 將供應商連線狀態列舉 (<see cref="EnumModbusConnectionState"/>) 轉換為系統合約連線狀態列舉 (<see cref="Contracts.ConnectionState"/>)。
    /// </summary>
    /// <param name="s">供應商連線狀態列舉值。</param>
    /// <returns>系統合約之連線狀態列舉值。</returns>
    public static Contracts.ConnectionState ToContract(this EnumModbusConnectionState s) => (Contracts.ConnectionState)s;
}
