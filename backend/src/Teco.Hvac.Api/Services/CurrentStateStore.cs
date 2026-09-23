using Teco.Hvac.Contracts;

namespace Teco.Hvac.Api.Services;

/// <summary>
/// 記憶體內的「目前狀態」，由 /internal/ingest 更新、GET /realtime/snapshot 讀取、
/// TelemetryHub 廣播共用。單站、單一 collector、5 秒一次更新，記憶體足夠，
/// 不引入 Redis（計畫決策：多一個元件的維運成本不划算）。
/// </summary>
public sealed class CurrentStateStore
{
    private readonly Lock _lock = new();
    private IngestPayload? _latest;
    private readonly Dictionary<Channel, DateTimeOffset> _lastSuccessAtUtc = new();
    private readonly Dictionary<Channel, ConnectionStatusPayload> _connectionStatus = new();

    public void UpdateData(IngestPayload payload)
    {
        lock (_lock)
        {
            _latest = payload;

            bool gatewaySuccess = payload.Hanbell1.ReadStatus == ReadStatus.Success ||
                                   payload.Hanbell2.ReadStatus == ReadStatus.Success;
            if (gatewaySuccess) _lastSuccessAtUtc[Channel.HanbellModbusGateway] = payload.UpdateTimeUtc;
            if (payload.Ddc1.ReadStatus == ReadStatus.Success) _lastSuccessAtUtc[Channel.Ddc1] = payload.UpdateTimeUtc;
            if (payload.Ddc2.ReadStatus == ReadStatus.Success) _lastSuccessAtUtc[Channel.Ddc2] = payload.UpdateTimeUtc;
        }
    }

    public void UpdateConnection(ConnectionStatusPayload payload)
    {
        lock (_lock) { _connectionStatus[payload.Channel] = payload; }
    }

    public IngestPayload? GetLatest()
    {
        lock (_lock) { return _latest; }
    }

    public bool TryGetConnectionStatus(Channel channel, out ConnectionStatusPayload status)
    {
        lock (_lock) { return _connectionStatus.TryGetValue(channel, out status!); }
    }

    /// <summary>
    /// 建構單一通道的資料品質描述。因為 Collector 沒有逐欄位時間戳，這裡只能提供
    /// 通道等級的新鮮度（見計畫 §7：每個回應都要帶 dataQuality）。
    /// </summary>
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
