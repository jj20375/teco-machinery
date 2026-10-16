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

    /// <summary>
    /// 冰水主機從停止變成運轉後，隔多少分鐘才開始評估出水溫／回水溫／溫差門檻（預設 15 分鐘）。
    /// 剛開機時冰水迴路還是環境溫度，主機要一段時間才拉到設定溫度，這段期間超標是正常現象。
    /// 現場量到實際的降溫時間後再調整（環境變數 <c>Collector__ChillerWarmupMinutes</c>）。
    /// </summary>
    public int ChillerWarmupMinutes { get; init; } = 15;

    /// <summary>通道超過此秒數沒有成功讀取，視為降級（寫入 channel_health + 觸發告警）。</summary>
    public int WatchdogStaleSeconds { get; init; } = 60;
    /// <summary>完全沒有收到任何 EventDataReceived 超過此秒數，觸發 Collector 自我重建。</summary>
    public int WatchdogRestartAfterSeconds { get; init; } = 300;
    /// <summary>
    /// 連續幾次逾時（重建後仍收不到資料事件）就結束程序，交給 Docker（restart: unless-stopped）重啟容器。
    /// 實測 DLL 卡住時只在程式內重建物件救不回來，要整個程序重啟才會恢復。下限 1（逾時就直接結束，不先重建）。
    /// </summary>
    public int WatchdogExitAfterConsecutiveTimeouts { get; init; } = 2;
    /// <summary>
    /// 僅供測試自我保護機制：丟棄所有資料事件，模擬 DLL 卡住（設備斷線時 DLL 仍會送資料事件，沒辦法用模擬器製造這種情況）。
    /// 正式環境一律保持 false。
    /// </summary>
    public bool DebugDropDataEvents { get; init; }

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
