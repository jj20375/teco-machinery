namespace Teco.Hvac.Collector;

/// <summary>
/// 採集器連線與節流組態選項 (<see cref="CollectorOptions"/>)。
/// 包含現場硬體網路連線參數、時序落地節流門檻以及看門狗自我修復機制之逾時參數。
/// </summary>
public sealed class CollectorOptions
{
    /// <summary>對應 appsettings.json 中的組態區段名稱。</summary>
    public const string SectionName = "Collector";

    /// <summary>漢鐘冰水主機 Modbus Gateway 網路連線設定（包含 IP、Port 與鮑率）。</summary>
    public required HanbellOptions Hanbell { get; init; }

    /// <summary>B1 樓層 DDC1 控制器網路連線設定。</summary>
    public required EndpointOptions Ddc1 { get; init; }

    /// <summary>B2 樓層 DDC2 控制器網路連線設定。</summary>
    public required EndpointOptions Ddc2 { get; init; }

    /// <summary>Web API 的內部通訊 BaseUrl（例如 http://api:8080），供即時遙測數據與連線狀態推播。</summary>
    public string? ApiBaseUrl { get; init; }

    /// <summary>與 Web API 內部攝取端點通訊使用的共享安全權杖（透過 X-Internal-Token 標頭傳遞）。</summary>
    public string? InternalToken { get; init; }

    /// <summary>FCU 時序資料落地節流秒數上限（預設 60 秒）；在此時間內若數值無變化則不重複寫入資料庫。</summary>
    public int FcuThrottleSeconds { get; init; } = 60;

    /// <summary>FCU 溫度變化落地門檻（預設 0.5°C）；溫度變化絕對值達此門檻時立即強制寫入資料庫，不受時間節流限制。</summary>
    public double FcuTemperatureChangeThreshold { get; init; } = 0.5;

    /// <summary>冰水主機時序資料落地節流秒數（預設 10 秒）。</summary>
    public int ChillerThrottleSeconds { get; init; } = 10;

    /// <summary>
    /// 冰水主機從停止變成運轉後，隔多少分鐘才開始評估出水溫／回水溫／溫差門檻（預設 15 分鐘）。
    /// 剛開機時冰水迴路還是環境溫度，主機要一段時間才拉到設定溫度，這段期間超標是正常現象。
    /// 現場量到實際的降溫時間後再調整（環境變數 <c>Collector__ChillerWarmupMinutes</c>）。
    /// </summary>
    public int ChillerWarmupMinutes { get; init; } = 15;

    /// <summary>通道超過此秒數完全未成功讀取時，判定該通道為降級狀態 (Degraded) 並觸發告警（預設 60 秒）。</summary>
    public int WatchdogStaleSeconds { get; init; } = 60;

    /// <summary>完全未收到任何資料事件超過此秒數時，判定採集核心內部線程卡死並觸發自我重建（預設 300 秒）。</summary>
    public int WatchdogRestartAfterSeconds { get; init; } = 300;

    /// <summary>
    /// 連續幾次逾時（自我重建後仍無法恢復事件接收）則主動終止處理程序交由 Docker 容器重啟（預設 2 次）。
    /// </summary>
    public int WatchdogExitAfterConsecutiveTimeouts { get; init; } = 2;

    /// <summary>
    /// 除錯測試開關：若設為 true 則強制丟棄所有資料事件，用以模擬 DLL 卡死與測試看門狗自我重建流程。正式機必須為 false。
    /// </summary>
    public bool DebugDropDataEvents { get; init; }

    /// <summary>
    /// 漢鐘冰水主機連線端點組態選項。
    /// </summary>
    public sealed class HanbellOptions
    {
        /// <summary>漢鐘 Modbus 閘道之 IPv4 位址。</summary>
        public required string Ip { get; init; }

        /// <summary>漢鐘 Modbus 閘道之 TCP 連接埠（例如 "502" 或 "4001"）。</summary>
        public required string Port { get; init; }

        /// <summary>RS-485 串列通訊鮑率（例如 "9600" 或 "19200"）。</summary>
        public required string BaudRate { get; init; }
    }

    /// <summary>
    /// 一般網路端點組態選項（適用於 DDC1 與 DDC2）。
    /// </summary>
    public sealed class EndpointOptions
    {
        /// <summary>設備控制器之 IPv4 位址。</summary>
        public required string Ip { get; init; }

        /// <summary>設備控制器之 TCP 連接埠。</summary>
        public required string Port { get; init; }
    }
}
