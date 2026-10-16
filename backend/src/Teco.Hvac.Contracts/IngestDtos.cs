namespace Teco.Hvac.Contracts;

/// <summary>
/// Collector 採集器推送至 Web API 內部攝取端點 (/internal/ingest/data) 的完整數據酬載物件。
/// 封裝單次輪詢所獲得之漢鐘 1/2 號主機快照與 DDC 1/2 控制器快照。
/// </summary>
public sealed class IngestPayload
{
    /// <summary>底層採集器標註之數據更新標準 UTC 時間戳記。</summary>
    public required DateTimeOffset UpdateTimeUtc { get; init; }

    /// <summary>Collector 服務接收到底層事件之標準 UTC 時間戳記。</summary>
    public required DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>漢鐘 1 號冰水主機之遙測快照。</summary>
    public required ChillerSnapshot Hanbell1 { get; init; }

    /// <summary>漢鐘 2 號冰水主機之遙測快照。</summary>
    public required ChillerSnapshot Hanbell2 { get; init; }

    /// <summary>B1 樓層 DDC1 控制器及轄下所有 FCU 之遙測快照。</summary>
    public required DdcSnapshot Ddc1 { get; init; }

    /// <summary>B2 樓層 DDC2 控制器及轄下所有 FCU 之遙測快照。</summary>
    public required DdcSnapshot Ddc2 { get; init; }
}

/// <summary>
/// Collector 採集器推送至 Web API 內部連線端點 (/internal/ingest/connection) 的連線狀態變更酬載物件。
/// </summary>
public sealed class ConnectionStatusPayload
{
    /// <summary>發生連線狀態變更的實體通訊通道。</summary>
    public required Channel Channel { get; init; }

    /// <summary>目標端點之 IPv4 位址。</summary>
    public required string Ip { get; init; }

    /// <summary>目標端點之 TCP 連接埠。</summary>
    public required string Port { get; init; }

    /// <summary>目前最新之連線狀態列舉值。</summary>
    public required ConnectionState ConnectionState { get; init; }

    /// <summary>是否已建立有效連線。</summary>
    public required bool IsConnected { get; init; }

    /// <summary>狀態變更觸發之標準 UTC 時間戳記。</summary>
    public required DateTimeOffset TriggerTimeUtc { get; init; }
}

/// <summary>
/// Web API 對外公開端點回傳數據之通用信封物件。
/// 附帶資料品質 (DataQuality) 與採集時間戳記，供前端介面評估數值可信度。
/// </summary>
/// <typeparam name="T">信封所包裝的具體業務數據型別。</typeparam>
public sealed class Envelope<T>
{
    /// <summary>設備採集數據更新的標準 UTC 時間戳記。</summary>
    public required DateTimeOffset UpdateTimeUtc { get; init; }

    /// <summary>後端伺服器接收到數據的標準 UTC 時間戳記。</summary>
    public required DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>該筆數據所屬通道之即時資料品質指標。</summary>
    public required DataQuality DataQuality { get; init; }

    /// <summary>信封內承載的實際數據本體。</summary>
    public required T Value { get; init; }
}

/// <summary>
/// 前端即時儀表板與 SignalR 廣播共用的全廠實時快照物件。
/// 包含全廠所有主機、DDC 與各通道資料品質，供監控頁面一次性渲染。
/// </summary>
public sealed class RealtimeSnapshot
{
    /// <summary>數據更新之標準 UTC 時間戳記。</summary>
    public required DateTimeOffset UpdateTimeUtc { get; init; }

    /// <summary>伺服器接收數據之標準 UTC 時間戳記。</summary>
    public required DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>漢鐘 1 號冰水主機最新快照。</summary>
    public required ChillerSnapshot Hanbell1 { get; init; }

    /// <summary>漢鐘 2 號冰水主機最新快照。</summary>
    public required ChillerSnapshot Hanbell2 { get; init; }

    /// <summary>B1 樓層 DDC1 最新快照。</summary>
    public required DdcSnapshot Ddc1 { get; init; }

    /// <summary>B2 樓層 DDC2 最新快照。</summary>
    public required DdcSnapshot Ddc2 { get; init; }

    /// <summary>三個實體通訊通道的最新資料品質字典，鍵值為 Channel 列舉。</summary>
    public required IReadOnlyDictionary<Channel, DataQuality> DataQuality { get; init; }
}
