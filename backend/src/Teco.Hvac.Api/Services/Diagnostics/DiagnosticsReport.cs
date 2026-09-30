namespace Teco.Hvac.Api.Services.Diagnostics;

/// <summary>GET /api/v1/platform/diagnostics 的回應。Level 一律是 ok / warn / error / unknown 字串。</summary>
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

public sealed record CollectorStatus(string Level, bool Reachable, string? Status, string Detail);

public sealed record IngestStatus(
    string Level,
    DateTimeOffset? LastIngestAtUtc,
    double? SecondsSinceLastIngest,
    long IngestCountSinceApiStart,
    /// <summary>供應商程式建立事件的時間（Collector 主機時鐘），不是設備量測時間（說明書 4.2）。</summary>
    DateTimeOffset? EventTimeUtc,
    DateTimeOffset? CollectorReceivedAtUtc);

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

public sealed record DiagnosticCheck(string Key, string Category, string Label, string Level, string Detail);

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

public sealed record ChillerFieldReport(
    int ModbusId,
    string? Code,
    string? Name,
    string Level,
    int ReadStatus,
    bool IsConnected,
    IReadOnlyList<ChillerFieldValue> Fields);

public sealed record ChillerFieldValue(
    string Key, string Label, string Unit, double Value, double? PreviousValue, double? Min, double? Max, string Level, string? Note);

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

public sealed record ConnectionHistoryItem(int Channel, string Name, int ConnectionState, int ReadStatus, DateTimeOffset ChangedAtUtc);

/// <summary>
/// 排程狀態。LastRun/History 只存在 API 記憶體（重啟即清空）；EvidenceUtc 是資料庫裡的持久證據
/// （例如 rollup 表最新整點），兩者對照才能分辨「API 剛重啟還沒跑」跟「排程壞了」。
/// </summary>
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
