namespace Teco.Hvac.Api.Services;

/// <summary>
/// 背景排程的執行紀錄，給平台「系統診斷」頁確認排程真的有在跑。
/// 原本排程成功/失敗只寫 log，現場要確認就得進容器翻 log；這裡只存記憶體（API 重啟即清空），
/// 持久證據另外靠資料庫本身（例如 rollup 表的最新整點），兩者在診斷頁並列顯示。
/// </summary>
public sealed class ScheduledJobStatusStore
{
    private const int HistoryLimit = 20;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, JobState> _jobs = new();

    public sealed record JobRun(
        DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, bool? Succeeded, string? Summary, string? Error);

    public sealed record JobStatus(
        string Key, string Name, TimeSpan Interval, JobRun? LastRun, DateTimeOffset? NextRunAtUtc,
        IReadOnlyList<JobRun> History);

    private sealed class JobState(string name, TimeSpan interval)
    {
        public string Name { get; } = name;
        public TimeSpan Interval { get; } = interval;
        public LinkedList<JobRun> History { get; } = new();
    }

    /// <summary>服務啟動時註冊，讓「還沒跑過任何一次」的排程也會出現在診斷頁上，而不是整列消失。</summary>
    public void Register(string key, string name, TimeSpan interval)
    {
        lock (_lock) { _jobs.TryAdd(key, new JobState(name, interval)); }
    }

    public void Start(string key)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(key, out var job)) return;
            job.History.AddFirst(new JobRun(DateTimeOffset.UtcNow, null, null, null, null));
            while (job.History.Count > HistoryLimit) job.History.RemoveLast();
        }
    }

    public void Succeed(string key, string summary) => Finish(key, true, summary, null);

    public void Fail(string key, Exception ex) => Finish(key, false, null, ex.Message);

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
