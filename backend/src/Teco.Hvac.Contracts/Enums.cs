namespace Teco.Hvac.Contracts;

/// <summary>
/// 實體通訊通道。對應 Collector 的 EnumTecoGolfConnectionChannel，
/// 但獨立定義以免 Contracts 直接依賴供應商 DLL。
/// </summary>
public enum Channel
{
    HanbellModbusGateway = 0,
    Ddc1 = 1,
    Ddc2 = 2,
}

/// <summary>對應 Collector 的 EnumModbusConnectionState。</summary>
public enum ConnectionState
{
    Stopped = 0,
    Connecting = 1,
    Connected = 2,
    ConnectFailed = 3,
    Disconnected = 4,
    Unknown = -1,
}

/// <summary>對應 Collector 的 EnumTecoGolfDataReadStatus。</summary>
public enum ReadStatus
{
    NotRead = 0,
    Success = 1,
    Failed = 2,
    Disconnected = 3,
}

/// <summary>對應 Collector 的 EnumHanbellWaterControl。</summary>
public enum WaterControl
{
    InletWater = 0,
    OutletWater = 1,
}

/// <summary>對應 Collector 的 EnumFCUSwitchStatus。</summary>
public enum FcuSwitchStatus
{
    Off = 0,
    On = 1,
    Unknown = -1,
}

/// <summary>對應 Collector 的 EnumFCUOperationMode。</summary>
public enum FcuOperationMode
{
    Cooling = 1,
    Heating = 2,
    Ventilation = 3,
    Unknown = -1,
}

/// <summary>對應 Collector 的 EnumFCUFanSpeed。</summary>
public enum FcuFanSpeed
{
    High = 0,
    Medium = 1,
    Low = 2,
    Auto = 3,
    Unknown = -1,
}

/// <summary>告警嚴重度。</summary>
public enum AlarmSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}
