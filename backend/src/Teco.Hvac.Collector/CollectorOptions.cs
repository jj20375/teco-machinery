namespace Teco.Hvac.Collector;

/// <summary>Collector 連線與節流參數。IP/Port 為現場配置，其餘為後端自訂的節流與看門狗門檻。</summary>
public sealed class CollectorOptions
{
    public const string SectionName = "Collector";

    public required HanbellOptions Hanbell { get; init; }
    public required EndpointOptions Ddc1 { get; init; }
    public required EndpointOptions Ddc2 { get; init; }

    /// <summary>Api 的 /internal/ingest 端點，供即時推播（不受下方節流影響，每次事件都送）。</summary>
    public string? ApiBaseUrl { get; init; }
    public string? InternalToken { get; init; }

    /// <summary>FCU 落地節流秒數；未變化時的最大間隔（計畫 P2 §寫入節流）。</summary>
    public int FcuThrottleSeconds { get; init; } = 60;
    /// <summary>FCU 溫度變化超過此值時，即使未到節流時間也立即補寫。</summary>
    public double FcuTemperatureChangeThreshold { get; init; } = 0.5;
    /// <summary>冰水主機落地節流秒數。</summary>
    public int ChillerThrottleSeconds { get; init; } = 10;

    /// <summary>通道超過此秒數沒有成功讀取，視為降級（寫入 channel_health + 觸發告警）。</summary>
    public int WatchdogStaleSeconds { get; init; } = 60;
    /// <summary>完全沒有收到任何 EventDataReceived 超過此秒數，觸發 Collector 自我重建。</summary>
    public int WatchdogRestartAfterSeconds { get; init; } = 300;

    public sealed class HanbellOptions
    {
        public required string Ip { get; init; }
        public required string Port { get; init; }
        public required string BaudRate { get; init; }
    }

    public sealed class EndpointOptions
    {
        public required string Ip { get; init; }
        public required string Port { get; init; }
    }
}
