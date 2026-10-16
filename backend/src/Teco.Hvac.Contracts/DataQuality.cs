namespace Teco.Hvac.Contracts;

/// <summary>
/// 通道資料品質描述物件 (<see cref="DataQuality"/>)。
/// 
/// 隨附於對外 API 回應與 SignalR 推播中，作為前端判定「畫面上數值是否依然有效可信」的核心憑據。
/// 
/// 【設計背景】
/// 由於硬體設備與底層採集器未提供逐欄位採集時間戳，當連線中斷或通訊失敗時，快照中的數值可能是舊快取或預設值；
/// 前端介面透過此品質指標的 StaleSeconds 與 ReadStatus，即可直觀呈現「數據過期」、「離線」或「讀取失敗」的視覺警告。
/// </summary>
public sealed class DataQuality
{
    /// <summary>該品質指標所屬之實體通訊通道。</summary>
    public required Channel Channel { get; init; }

    /// <summary>通訊通道目前是否處於連線狀態。</summary>
    public required bool IsConnected { get; init; }

    /// <summary>本通訊通道最近一次輪詢之讀取結果狀態（成功、失敗或中斷）。</summary>
    public required ReadStatus ReadStatus { get; init; }

    /// <summary>該通道最後一次成功讀取 (ReadStatus.Success) 數據的標準 UTC 時間戳記；若自啟動以來未曾成功則為 null。</summary>
    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    /// <summary>距離最後一次成功讀取所經過的秒數（過期秒數）；可用於評估資料陳舊度。</summary>
    public double? StaleSeconds { get; init; }
}
