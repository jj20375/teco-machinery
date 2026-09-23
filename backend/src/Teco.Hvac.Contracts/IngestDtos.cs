namespace Teco.Hvac.Contracts;

/// <summary>Collector → Api 的內部 ingest 酬載，對應說明書 EventArgsDataReceived 的四組快照。</summary>
public sealed class IngestPayload
{
    public required DateTimeOffset UpdateTimeUtc { get; init; }
    public required DateTimeOffset ReceivedAtUtc { get; init; }
    public required ChillerSnapshot Hanbell1 { get; init; }
    public required ChillerSnapshot Hanbell2 { get; init; }
    public required DdcSnapshot Ddc1 { get; init; }
    public required DdcSnapshot Ddc2 { get; init; }
}

/// <summary>Collector → Api 的連線狀態變化通知，對應 EventArgsConnectStatus。</summary>
public sealed class ConnectionStatusPayload
{
    public required Channel Channel { get; init; }
    public required string Ip { get; init; }
    public required string Port { get; init; }
    public required ConnectionState ConnectionState { get; init; }
    public required bool IsConnected { get; init; }
    public required DateTimeOffset TriggerTimeUtc { get; init; }
}

/// <summary>對外 API 回應的通用信封，附帶資料品質。</summary>
public sealed class Envelope<T>
{
    public required DateTimeOffset UpdateTimeUtc { get; init; }
    public required DateTimeOffset ReceivedAtUtc { get; init; }
    public required DataQuality DataQuality { get; init; }
    public required T Value { get; init; }
}

/// <summary>GET /api/v1/realtime/snapshot 與 SignalR 廣播共用的完整快照，附三個通道的資料品質。</summary>
public sealed class RealtimeSnapshot
{
    public required DateTimeOffset UpdateTimeUtc { get; init; }
    public required DateTimeOffset ReceivedAtUtc { get; init; }
    public required ChillerSnapshot Hanbell1 { get; init; }
    public required ChillerSnapshot Hanbell2 { get; init; }
    public required DdcSnapshot Ddc1 { get; init; }
    public required DdcSnapshot Ddc2 { get; init; }
    public required IReadOnlyDictionary<Channel, DataQuality> DataQuality { get; init; }
}
