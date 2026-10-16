using Teco.Hvac.Contracts;

namespace Teco.Hvac.Api.Services;

/// <summary>
/// 記憶體內的「目前狀態」儲存庫。
/// 由 /internal/ingest 更新、GET /realtime/snapshot 讀取、
/// TelemetryHub 廣播共用。單站、單一 collector、5 秒一次更新，記憶體足夠，
/// 不引入 Redis（計畫決策：多一個元件的維運成本不划算）。
/// </summary>
public sealed class CurrentStateStore
{
    /// <summary>多執行緒讀寫互斥鎖。</summary>
    private readonly Lock _lock = new();
    /// <summary>最新一筆自 Collector 接收之 IngestPayload 資料。</summary>
    private IngestPayload? _latest;
    /// <summary>前一筆 IngestPayload 資料（用於比對累計值倒退與時間戳停滯）。</summary>
    private IngestPayload? _previous;
    /// <summary>API 本地伺服器收到最後一筆 IngestPayload 的時間戳 (UTC)。</summary>
    private DateTimeOffset? _lastIngestAtUtc;
    /// <summary>自服務啟動以來累計收到的資料包總數。</summary>
    private long _ingestCount;
    /// <summary>各實體通訊通道最後一次成功讀取的時間戳記對照表 (UTC)。</summary>
    private readonly Dictionary<Channel, DateTimeOffset> _lastSuccessAtUtc = new();
    /// <summary>各實體通訊通道的最新連線狀態快照。</summary>
    private readonly Dictionary<Channel, ConnectionStatusPayload> _connectionStatus = new();

    /// <summary>
    /// 更新最新設備讀值資料包，並重新整理各通道最新成功時間與計數。
    /// </summary>
    /// <param name="payload">來自 Collector 的完整遙測資料包。</param>
    public void UpdateData(IngestPayload payload)
    {
        lock (_lock)
        {
            _previous = _latest;
            _latest = payload;
            _lastIngestAtUtc = DateTimeOffset.UtcNow;
            _ingestCount++;

            // 漢鐘主機閘道通道：任一主機讀取成功即代表通訊正常
            bool gatewaySuccess = payload.Hanbell1.ReadStatus == ReadStatus.Success ||
                                   payload.Hanbell2.ReadStatus == ReadStatus.Success;
            if (gatewaySuccess) _lastSuccessAtUtc[Channel.HanbellModbusGateway] = payload.UpdateTimeUtc;
            if (payload.Ddc1.ReadStatus == ReadStatus.Success) _lastSuccessAtUtc[Channel.Ddc1] = payload.UpdateTimeUtc;
            if (payload.Ddc2.ReadStatus == ReadStatus.Success) _lastSuccessAtUtc[Channel.Ddc2] = payload.UpdateTimeUtc;
        }
    }

    /// <summary>
    /// 更新指定通道的實體連線狀態（由 Collector Watchdog 回報）。
    /// </summary>
    /// <param name="payload">通道連線狀態資料包。</param>
    public void UpdateConnection(ConnectionStatusPayload payload)
    {
        lock (_lock) { _connectionStatus[payload.Channel] = payload; }
    }

    /// <summary>
    /// 取得記憶體中最新一筆接收到的 IngestPayload 資料。
    /// </summary>
    /// <returns>最新資料包；若尚未收到任何資料則為 null。</returns>
    public IngestPayload? GetLatest()
    {
        lock (_lock) { return _latest; }
    }

    /// <summary>
    /// 給平台診斷頁用：前一筆快照（比對累計值有沒有倒退、UpdateTime 有沒有停住）、
    /// API 自己收到最後一筆的時間（不信任 Collector 回報的時間，才抓得到時鐘偏移）與累計筆數。
    /// </summary>
    /// <returns>包含最新包、前一包、API 接收時間與累計筆數的狀態元組。</returns>
    public (IngestPayload? Latest, IngestPayload? Previous, DateTimeOffset? LastIngestAtUtc, long IngestCount) GetIngestState()
    {
        lock (_lock) { return (_latest, _previous, _lastIngestAtUtc, _ingestCount); }
    }

    /// <summary>
    /// 嘗試取得特定通道的最新連線狀態快照。
    /// </summary>
    /// <param name="channel">目標通訊通道。</param>
    /// <param name="status">輸出之連線狀態資料包。</param>
    /// <returns>若存在對應通道之紀錄則為 true；否則為 false。</returns>
    public bool TryGetConnectionStatus(Channel channel, out ConnectionStatusPayload status)
    {
        lock (_lock) { return _connectionStatus.TryGetValue(channel, out status!); }
    }

    /// <summary>
    /// 建構單一通道的資料品質描述。因為 Collector 沒有逐欄位時間戳，這裡只能提供
    /// 通道等級的新鮮度（見計畫 §7：每個回應都要帶 dataQuality）。
    /// </summary>
    /// <param name="channel">目標通訊通道。</param>
    /// <param name="readStatus">該通道的即時讀取狀態。</param>
    /// <returns>封裝完成的 <see cref="DataQuality"/> 物件。</returns>
    public DataQuality BuildDataQuality(Channel channel, ReadStatus readStatus)
    {
        lock (_lock)
        {
            _lastSuccessAtUtc.TryGetValue(channel, out var last);
            DateTimeOffset? lastNullable = last == default ? null : last;
            _connectionStatus.TryGetValue(channel, out var conn);
            double? stale = lastNullable.HasValue
                ? (DateTimeOffset.UtcNow - lastNullable.Value).TotalSeconds
                : null;

            return new DataQuality
            {
                Channel = channel,
                IsConnected = conn?.IsConnected ?? false,
                ReadStatus = readStatus,
                LastSuccessAtUtc = lastNullable,
                StaleSeconds = stale,
            };
        }
    }

    /// <summary>
    /// 依據最新接收的資料與各通道品質指標，組裝前台專用的完整即時快照模型。
    /// </summary>
    /// <returns>包含四部設備與資料品質指標的 <see cref="RealtimeSnapshot"/>；若無最新資料則為 null。</returns>
    public RealtimeSnapshot? BuildRealtimeSnapshot()
    {
        var latest = GetLatest();
        if (latest is null) return null;

        var quality = new Dictionary<Channel, DataQuality>
        {
            [Channel.HanbellModbusGateway] = BuildDataQuality(
                Channel.HanbellModbusGateway,
                latest.Hanbell1.ReadStatus == ReadStatus.Success || latest.Hanbell2.ReadStatus == ReadStatus.Success
                    ? ReadStatus.Success
                    : latest.Hanbell1.ReadStatus),
            [Channel.Ddc1] = BuildDataQuality(Channel.Ddc1, latest.Ddc1.ReadStatus),
            [Channel.Ddc2] = BuildDataQuality(Channel.Ddc2, latest.Ddc2.ReadStatus),
        };

        return new RealtimeSnapshot
        {
            UpdateTimeUtc = latest.UpdateTimeUtc,
            ReceivedAtUtc = latest.ReceivedAtUtc,
            Hanbell1 = latest.Hanbell1,
            Hanbell2 = latest.Hanbell2,
            Ddc1 = latest.Ddc1,
            Ddc2 = latest.Ddc2,
            DataQuality = quality,
        };
    }
}
