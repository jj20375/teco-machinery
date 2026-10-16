namespace Teco.Hvac.Contracts;

/// <summary>
/// 單台 FCU（室內風機箱/送風機）之位置定位與運轉狀態快照物件。
/// 
/// 【唯一識別複合鍵】
/// FCU 在實體網路中以 (Channel, StationId, Position) 作為唯一複合鍵；
/// 不可單純依賴 Id 字串進行設備定位，因為硬體命名規則中（如 FC_MC1_01）可能跨不同 DDC 樓層控制器重複出現。
/// </summary>
public sealed class FcuSnapshot
{
    /// <summary>該 FCU 所屬之實體通訊通道（DDC1 或 DDC2）。</summary>
    public required Channel Channel { get; init; }

    /// <summary>所屬 DDC 控制器之站號（Station ID，數值 0 ~ 255）。</summary>
    public required byte StationId { get; init; }

    /// <summary>該 FCU 於該站控制器內部之位置序號（Position 索引，自 0 起算）。</summary>
    public required int Position { get; init; }

    /// <summary>設備原始識別字串（如 "FC_MC1_01"），供介面顯示與圖面定位使用。</summary>
    public required string Id { get; init; }

    /// <summary>硬體 Modbus 或內部定址位址。</summary>
    public required ushort Address { get; init; }

    /// <summary>FCU 目前電源開關狀態（開機 ON 或關機 OFF）。</summary>
    public FcuSwitchStatus SwitchStatus { get; init; }

    /// <summary>FCU 目前運轉模式（冷房、暖房或送風）。</summary>
    public FcuOperationMode Mode { get; init; }

    /// <summary>FCU 目前風速段位（強、中、弱或自動）。</summary>
    public FcuFanSpeed FanSpeed { get; init; }

    /// <summary>FCU 溫度感測器量測之室內實際環境溫度（單位：°C）。</summary>
    public double Temperature { get; init; }
}

/// <summary>
/// 單座 DDC（樓層控制器）之通訊狀態與所轄所有 FCU 集合快照物件。
/// </summary>
public sealed class DdcSnapshot
{
    /// <summary>所屬通訊通道（DDC1 負責 B1 樓層，DDC2 負責 B2 樓層）。</summary>
    public required Channel Channel { get; init; }

    /// <summary>底層控制器採集數據更新的標準 UTC 時間戳記。</summary>
    public required DateTimeOffset UpdateTimeUtc { get; init; }

    /// <summary>本輪 DDC 輪詢讀取之通訊結果狀態（成功、失敗或未讀）。</summary>
    public required ReadStatus ReadStatus { get; init; }

    /// <summary>DDC 控制器目前是否處於通訊連線狀態。</summary>
    public required bool IsConnected { get; init; }

    /// <summary>該 DDC 控制器所轄之所有 FCU 送風機快照清單。</summary>
    public required IReadOnlyList<FcuSnapshot> FcuList { get; init; }
}
