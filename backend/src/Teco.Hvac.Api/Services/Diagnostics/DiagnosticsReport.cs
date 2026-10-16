namespace Teco.Hvac.Api.Services.Diagnostics;

/// <summary>
/// 系統診斷報告完整模型。
/// GET /api/v1/platform/diagnostics 的回應。Level 一律是 ok / warn / error / unknown 字串。
/// </summary>
/// <param name="GeneratedAtUtc">診斷報告產生時間 (UTC)。</param>
/// <param name="Overall">全系統整體健康等級 ("ok", "warn", "error")。</param>
/// <param name="Collector">Collector 程式本體健康狀態。</param>
/// <param name="Ingest">API 端資料接收管線狀態。</param>
/// <param name="Channels">三條通訊實體通道狀態清單。</param>
/// <param name="Checks">診斷規則自動檢核項目清單。</param>
/// <param name="FcuCoverage">FCU 設備讀取覆蓋率與配置匹配報告。</param>
/// <param name="Chillers">兩台冰水主機數值品質與遞增計數檢查報告。</param>
/// <param name="Persistence">資料庫落地與分區寫入狀態。</param>
/// <param name="Jobs">背景排程任務執行狀態。</param>
/// <param name="ConnectionHistory">通訊連線歷史異動事件。</param>
public sealed record DiagnosticsReport(
    DateTimeOffset GeneratedAtUtc,
    string Overall,
    CollectorStatus Collector,
    IngestStatus Ingest,
    IReadOnlyList<ChannelStatus> Channels,
    IReadOnlyList<DiagnosticCheck> Checks,
    IReadOnlyList<FcuCoverage> FcuCoverage,
    IReadOnlyList<ChillerFieldReport> Chillers,
    PersistenceStatus? Persistence,
    IReadOnlyList<JobReport> Jobs,
    IReadOnlyList<ConnectionHistoryItem> ConnectionHistory);

/// <summary>Collector 程式健康狀態。</summary>
/// <param name="Level">健康等級。</param>
/// <param name="Reachable">是否可經由 HTTP 探測連通。</param>
/// <param name="Status">健康狀態碼（例如 "healthy", "degraded"）。</param>
/// <param name="Detail">詳細說明文字。</param>
public sealed record CollectorStatus(string Level, bool Reachable, string? Status, string Detail);

/// <summary>API 遙測資料接收管線狀態。</summary>
/// <param name="Level">接收健康等級。</param>
/// <param name="LastIngestAtUtc">最後接收資料時間 (UTC)。</param>
/// <param name="SecondsSinceLastIngest">距離上次接收經過秒數。</param>
/// <param name="IngestCountSinceApiStart">自 API 啟動以來累計接收筆數。</param>
/// <param name="EventTimeUtc">供應商程式建立事件的時間（Collector 主機時鐘），不是設備量測時間（說明書 4.2）。</param>
/// <param name="CollectorReceivedAtUtc">Collector 本地接收時間。</param>
public sealed record IngestStatus(
    string Level,
    DateTimeOffset? LastIngestAtUtc,
    double? SecondsSinceLastIngest,
    long IngestCountSinceApiStart,
    DateTimeOffset? EventTimeUtc,
    DateTimeOffset? CollectorReceivedAtUtc);

/// <summary>單一通訊通道狀態。</summary>
/// <param name="Channel">通道識別碼。</param>
/// <param name="Name">通道中文名稱。</param>
/// <param name="Level">健康等級。</param>
/// <param name="Ip">現場閘道 IP。</param>
/// <param name="Port">現場連接埠。</param>
/// <param name="ConnectionState">底層連線狀態碼。</param>
/// <param name="IsConnected">是否連線中。</param>
/// <param name="ReadStatus">最新讀取狀態碼。</param>
/// <param name="LastSuccessAtUtc">最後成功通訊時間 (UTC)。</param>
/// <param name="StaleSeconds">陳舊秒數。</param>
/// <param name="LastConnectionChangeAtUtc">最後連線狀態變更時間。</param>
public sealed record ChannelStatus(
    int Channel,
    string Name,
    string Level,
    string? Ip,
    string? Port,
    int? ConnectionState,
    bool IsConnected,
    int? ReadStatus,
    DateTimeOffset? LastSuccessAtUtc,
    double? StaleSeconds,
    DateTimeOffset? LastConnectionChangeAtUtc);

/// <summary>診斷項目檢驗結果。</summary>
/// <param name="Key">檢驗項目鍵值。</param>
/// <param name="Category">所屬分類。</param>
/// <param name="Label">項目名稱。</param>
/// <param name="Level">檢查結果等級。</param>
/// <param name="Detail">診斷說明或建議。</param>
public sealed record DiagnosticCheck(string Key, string Category, string Label, string Level, string Detail);

/// <summary>FCU 讀取覆蓋率與對照狀態。</summary>
/// <param name="Channel">所屬通訊通道。</param>
/// <param name="Name">通道名稱。</param>
/// <param name="Level">覆蓋率健康等級。</param>
/// <param name="Received">接收到的設備讀值筆數。</param>
/// <param name="Mapped">已在資料庫建檔的設備數。</param>
/// <param name="Matched">成功比對且有數據的設備數。</param>
/// <param name="Unresponsive">無回應的設備數。</param>
/// <param name="TemperatureOutOfRange">溫度超出正常範圍的設備數。</param>
/// <param name="UnmappedKeys">收到但資料庫查無對照之鍵值清單。</param>
/// <param name="MissingKeys">資料庫有建檔但未收到讀值之鍵值清單。</param>
public sealed record FcuCoverage(
    int Channel,
    string Name,
    string Level,
    int Received,
    int Mapped,
    int Matched,
    int Unresponsive,
    int TemperatureOutOfRange,
    IReadOnlyList<string> UnmappedKeys,
    IReadOnlyList<string> MissingKeys);

/// <summary>單一冰水主機數值診斷報告。</summary>
/// <param name="ModbusId">Modbus 站號。</param>
/// <param name="Code">主機代碼。</param>
/// <param name="Name">顯示名稱。</param>
/// <param name="Level">健康等級。</param>
/// <param name="ReadStatus">讀取狀態碼。</param>
/// <param name="IsConnected">是否連線中。</param>
/// <param name="Fields">各遙測欄位檢驗結果。</param>
public sealed record ChillerFieldReport(
    int ModbusId,
    string? Code,
    string? Name,
    string Level,
    int ReadStatus,
    bool IsConnected,
    IReadOnlyList<ChillerFieldValue> Fields);

/// <summary>冰水主機單一遙測欄位檢查值。</summary>
/// <param name="Key">欄位代碼。</param>
/// <param name="Label">欄位中文名稱。</param>
/// <param name="Unit">單位。</param>
/// <param name="Value">當前數值。</param>
/// <param name="PreviousValue">前一筆數值。</param>
/// <param name="Min">正常範圍下限。</param>
/// <param name="Max">正常範圍上限。</param>
/// <param name="Level">品質等級。</param>
/// <param name="Note">備註說明。</param>
public sealed record ChillerFieldValue(
    string Key, string Label, string Unit, double Value, double? PreviousValue, double? Min, double? Max, string Level, string? Note);

/// <summary>資料庫落地儲存狀態。</summary>
/// <param name="Level">健康等級。</param>
/// <param name="RecentWindowMinutes">近期視窗分鐘數。</param>
/// <param name="ChillerLatestTsUtc">冰水主機最新落地時間 (UTC)。</param>
/// <param name="ChillerRecentRows">近期冰水主機寫入筆數。</param>
/// <param name="ChillerRecentDevices">近期有寫入的冰水主機台數。</param>
/// <param name="ChillerActiveDevices">啟用中的冰水主機總數。</param>
/// <param name="FcuLatestTsUtc">FCU 最新落地時間 (UTC)。</param>
/// <param name="FcuRecentRows">近期 FCU 寫入筆數。</param>
/// <param name="FcuRecentDevices">近期有寫入的 FCU 台數。</param>
/// <param name="FcuActiveDevices">啟用中的 FCU 總台數。</param>
/// <param name="RollupChillerLatestBucketUtc">冰水主機彙總表最新整點時間 (UTC)。</param>
/// <param name="RollupFcuLatestBucketUtc">FCU 彙總表最新整點時間 (UTC)。</param>
/// <param name="Error">查詢異常訊息。</param>
public sealed record PersistenceStatus(
    string Level,
    int RecentWindowMinutes,
    DateTimeOffset? ChillerLatestTsUtc,
    long ChillerRecentRows,
    long ChillerRecentDevices,
    long ChillerActiveDevices,
    DateTimeOffset? FcuLatestTsUtc,
    long FcuRecentRows,
    long FcuRecentDevices,
    long FcuActiveDevices,
    DateTimeOffset? RollupChillerLatestBucketUtc,
    DateTimeOffset? RollupFcuLatestBucketUtc,
    string? Error);

/// <summary>
/// 通道連線歷史項目。
/// DurationSeconds：這段狀態持續多久，下一段開始時才回填；null＝仍在持續中（或回填機制上線前的舊資料）。
/// </summary>
/// <param name="Channel">通道識別碼。</param>
/// <param name="Name">通道中文名稱。</param>
/// <param name="ConnectionState">連線狀態碼。</param>
/// <param name="ReadStatus">讀取狀態碼。</param>
/// <param name="ChangedAtUtc">狀態變更時間 (UTC)。</param>
/// <param name="DurationSeconds">持續秒數。</param>
public sealed record ConnectionHistoryItem(
    int Channel, string Name, int ConnectionState, int ReadStatus, DateTimeOffset ChangedAtUtc, int? DurationSeconds);

/// <summary>
/// 排程狀態。LastRun/History 只存在 API 記憶體（重啟即清空）；EvidenceUtc 是資料庫裡的持久證據
/// （例如 rollup 表最新整點），兩者對照才能分辨「API 剛重啟還沒跑」跟「排程壞了」。
/// </summary>
/// <param name="Key">排程鍵值。</param>
/// <param name="Name">排程名稱。</param>
/// <param name="Level">健康等級。</param>
/// <param name="IntervalSeconds">間隔秒數。</param>
/// <param name="LastRun">最近一次執行結果。</param>
/// <param name="NextRunAtUtc">預估下一次執行時間 (UTC)。</param>
/// <param name="EvidenceLabel">持久性證據標籤名稱。</param>
/// <param name="EvidenceUtc">持久性證據時間戳 (UTC)。</param>
/// <param name="History">最近歷史紀錄清單。</param>
public sealed record JobReport(
    string Key,
    string Name,
    string Level,
    int IntervalSeconds,
    ScheduledJobStatusStore.JobRun? LastRun,
    DateTimeOffset? NextRunAtUtc,
    string? EvidenceLabel,
    DateTimeOffset? EvidenceUtc,
    IReadOnlyList<ScheduledJobStatusStore.JobRun> History);
