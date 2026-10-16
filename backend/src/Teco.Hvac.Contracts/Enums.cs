namespace Teco.Hvac.Contracts;

/// <summary>
/// 實體通訊通道列舉 (<see cref="Channel"/>)。
/// 對應底層採集器之 EnumTecoGolfConnectionChannel，但獨立於 Contracts 模組定義以解除對第三方 DLL 的直接依賴。
/// </summary>
public enum Channel
{
    /// <summary>漢鐘冰水主機 Modbus Gateway 閘道通道（通道 0）。</summary>
    HanbellModbusGateway = 0,
    /// <summary>B1 樓層 DDC1 區域控制器通訊通道（通道 1）。</summary>
    Ddc1 = 1,
    /// <summary>B2 樓層 DDC2 區域控制器通訊通道（通道 2）。</summary>
    Ddc2 = 2,
}

/// <summary>
/// 網路連線狀態列舉 (<see cref="ConnectionState"/>)。
/// 對應底層採集器之 EnumModbusConnectionState。
/// </summary>
public enum ConnectionState
{
    /// <summary>通訊通道已手動停止或尚未啟動。</summary>
    Stopped = 0,
    /// <summary>通訊通道正在嘗試建立 TCP 連線中（過渡狀態）。</summary>
    Connecting = 1,
    /// <summary>通訊通道已成功連線並可正常通訊。</summary>
    Connected = 2,
    /// <summary>通訊通道連線建立失敗（例如目標 IP 或連接埠拒絕連線）。</summary>
    ConnectFailed = 3,
    /// <summary>原先建立之連線已異常中斷斷開。</summary>
    Disconnected = 4,
    /// <summary>未知或尚未初始化的連線狀態。</summary>
    Unknown = -1,
}

/// <summary>
/// 資料讀取狀態列舉 (<see cref="ReadStatus"/>)。
/// 對應底層採集器之 EnumTecoGolfDataReadStatus。
/// </summary>
public enum ReadStatus
{
    /// <summary>尚未進行讀取作業（例如剛啟動或連線中）。</summary>
    NotRead = 0,
    /// <summary>本輪輪詢通訊讀取成功，回傳數據真實有效。</summary>
    Success = 1,
    /// <summary>本輪輪詢通訊讀取失敗（超時或校驗錯誤，此時數值不可信）。</summary>
    Failed = 2,
    /// <summary>通道處於離線斷線狀態，無法進行讀取。</summary>
    Disconnected = 3,
}

/// <summary>
/// 漢鐘冰水主機水流溫度控制基準模式列舉 (<see cref="WaterControl"/>)。
/// 對應底層採集器之 EnumHanbellWaterControl。
/// </summary>
public enum WaterControl
{
    /// <summary>進水水溫控制模式（以進水/回水溫度為溫控基準）。</summary>
    InletWater = 0,
    /// <summary>出水水溫控制模式（以出水/供水溫度為溫控基準）。</summary>
    OutletWater = 1,
}

/// <summary>
/// FCU 送風機電源開關狀態列舉 (<see cref="FcuSwitchStatus"/>)。
/// 對應底層採集器之 EnumFCUSwitchStatus。
/// </summary>
public enum FcuSwitchStatus
{
    /// <summary>設備處於關機停止狀態 (OFF)。</summary>
    Off = 0,
    /// <summary>設備處於開機運轉狀態 (ON)。</summary>
    On = 1,
    /// <summary>開關狀態未知。</summary>
    Unknown = -1,
}

/// <summary>
/// FCU 送風機運轉模式列舉 (<see cref="FcuOperationMode"/>)。
/// 對應底層採集器之 EnumFCUOperationMode。
/// </summary>
public enum FcuOperationMode
{
    /// <summary>冷房模式 (Cooling)。</summary>
    Cooling = 1,
    /// <summary>暖房模式 (Heating)。</summary>
    Heating = 2,
    /// <summary>送風/通風模式 (Ventilation)。</summary>
    Ventilation = 3,
    /// <summary>運轉模式未知。</summary>
    Unknown = -1,
}

/// <summary>
/// FCU 送風機風速段位列舉 (<see cref="FcuFanSpeed"/>)。
/// 對應底層採集器之 EnumFCUFanSpeed。
/// </summary>
public enum FcuFanSpeed
{
    /// <summary>強風段 (High)。</summary>
    High = 0,
    /// <summary>中風段 (Medium)。</summary>
    Medium = 1,
    /// <summary>弱風段 (Low)。</summary>
    Low = 2,
    /// <summary>自動風速調整 (Auto)。</summary>
    Auto = 3,
    /// <summary>風速狀態未知。</summary>
    Unknown = -1,
}

/// <summary>
/// 系統告警嚴重度等級列舉 (<see cref="AlarmSeverity"/>)。
/// </summary>
public enum AlarmSeverity
{
    /// <summary>一般資訊提示（非緊急事件）。</summary>
    Info = 0,
    /// <summary>警告層級（例如溫度輕微超標或保養提醒）。</summary>
    Warning = 1,
    /// <summary>嚴重緊急層級（例如設備硬體故障、跳脫或通訊中斷）。</summary>
    Critical = 2,
}
