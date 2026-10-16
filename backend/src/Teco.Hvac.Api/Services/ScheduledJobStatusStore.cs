namespace Teco.Hvac.Api.Services;

/// <summary>
/// 背景排程的執行紀錄儲存庫，給平台「系統診斷」頁確認排程真的有在跑。
/// 原本排程成功/失敗只寫 log，現場要確認就得進容器翻 log；這裡只存記憶體（API 重啟即清空），
/// 持久證據另外靠資料庫本身（例如 rollup 表的最新整點），兩者在診斷頁並列顯示。
/// </summary>
public sealed class ScheduledJobStatusStore
{
    /// <summary>各排程在記憶體內保留的最近執行紀錄筆數上限 (20 筆)。</summary>
    private const int HistoryLimit = 20;

    /// <summary>多執行緒讀寫互斥鎖。</summary>
    private readonly Lock _lock = new();
    /// <summary>排程工作狀態註冊表（Key 為排程識別鍵）。</summary>
    private readonly Dictionary<string, JobState> _jobs = new();

    /// <summary>
    /// 單次排程作業執行紀錄。
    /// </summary>
    /// <param name="StartedAtUtc">排程啟動時間 (UTC)。</param>
    /// <param name="FinishedAtUtc">排程結束時間 (UTC)；尚未完成時為 null。</param>
    /// <param name="Succeeded">是否執行成功；尚未完成時為 null。</param>
    /// <param name="Summary">執行摘要說明（例如 "已聚合 120 筆"）。</param>
    /// <param name="Error">若失敗時的異常訊息。</param>
    public sealed record JobRun(
        DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, bool? Succeeded, string? Summary, string? Error);

    /// <summary>
    /// 排程工作彙總狀態（供前端診斷儀表板呈現）。
    /// </summary>
    /// <param name="Key">排程唯一鍵值。</param>
    /// <param name="Name">排程可讀名稱。</param>
    /// <param name="Interval">設定之週期時間間隔。</param>
    /// <param name="LastRun">最近一次執行紀錄。</param>
    /// <param name="NextRunAtUtc">預估下一次執行時間 (UTC)。</param>
    /// <param name="History">最近歷史執行清單（由新到舊）。</param>
    public sealed record JobStatus(
        string Key, string Name, TimeSpan Interval, JobRun? LastRun, DateTimeOffset? NextRunAtUtc,
        IReadOnlyList<JobRun> History);

    /// <summary>
    /// 排程作業內部狀態容器。
    /// </summary>
    /// <param name="name">排程名稱。</param>
    /// <param name="interval">排程間隔。</param>
    private sealed class JobState(string name, TimeSpan interval)
    {
        /// <summary>排程顯示名稱。</summary>
        public string Name { get; } = name;
        /// <summary>週期時間間隔。</summary>
        public TimeSpan Interval { get; } = interval;
        /// <summary>雙向鏈結串列儲存最近執行紀錄。</summary>
        public LinkedList<JobRun> History { get; } = new();
    }

    /// <summary>
    /// 服務啟動時註冊，讓「還沒跑過任何一次」的排程也會出現在診斷頁上，而不是整列消失。
    /// </summary>
    /// <param name="key">排程唯一識別鍵。</param>
    /// <param name="name">排程顯示名稱。</param>
    /// <param name="interval">排程執行週期。</param>
    public void Register(string key, string name, TimeSpan interval)
    {
        lock (_lock) { _jobs.TryAdd(key, new JobState(name, interval)); }
    }

    /// <summary>
    /// 標記排程開始執行，並將一筆執行中的記錄推入歷史頂端。
    /// </summary>
    /// <param name="key">排程唯一識別鍵。</param>
    public void Start(string key)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(key, out var job)) return;
            job.History.AddFirst(new JobRun(DateTimeOffset.UtcNow, null, null, null, null));
            while (job.History.Count > HistoryLimit) job.History.RemoveLast();
        }
    }

    /// <summary>
    /// 標記目前執行中的排程成功結束。
    /// </summary>
    /// <param name="key">排程唯一識別鍵。</param>
    /// <param name="summary">執行結果摘要。</param>
    public void Succeed(string key, string summary) => Finish(key, true, summary, null);

    /// <summary>
    /// 標記目前執行中的排程失敗。
    /// </summary>
    /// <param name="key">排程唯一識別鍵。</param>
    /// <param name="ex">捕獲的異常例外實體。</param>
    public void Fail(string key, Exception ex) => Finish(key, false, null, ex.Message);

    /// <summary>
    /// 內部統一完成目前執行紀錄的輔助函式。
    /// </summary>
    /// <param name="key">排程唯一識別鍵。</param>
    /// <param name="succeeded">是否成功。</param>
    /// <param name="summary">摘要文字。</param>
    /// <param name="error">錯誤訊息。</param>
    private void Finish(string key, bool succeeded, string? summary, string? error)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(key, out var job) || job.History.First is null) return;
            job.History.First.Value = job.History.First.Value with
            {
                FinishedAtUtc = DateTimeOffset.UtcNow, Succeeded = succeeded, Summary = summary, Error = error,
            };
        }
    }

    /// <summary>
    /// 產生所有已註冊排程的即時快照列表。
    /// </summary>
    /// <returns>所有排程目前狀態的唯讀列表。</returns>
    public IReadOnlyList<JobStatus> Snapshot()
    {
        lock (_lock)
        {
            return _jobs.Select(kv =>
            {
                var last = kv.Value.History.First?.Value;
                return new JobStatus(
                    kv.Key, kv.Value.Name, kv.Value.Interval, last,
                    last is null ? null : last.StartedAtUtc + kv.Value.Interval,
                    kv.Value.History.ToList());
            }).ToList();
        }
    }
}
