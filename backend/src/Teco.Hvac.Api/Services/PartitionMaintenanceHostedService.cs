using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Services;

/// <summary>
/// 資料保留策略（見 docs/BACKEND_INTEGRATION_PLAN.md §6.3）：`fcu_reading` 預設保留 90 天、
/// `chiller_reading` 預設保留 180 天原始讀值，之後只留 rollup 永久保留。保留天數可用 `.env` 的
/// `RETENTION_FCU_DAYS`／`RETENTION_CHILLER_DAYS` 調整（見 <see cref="ResolveRetentionDays"/>）。分兩件事：
/// 1. 分割區增補——`p_future` 是目前分割表的「兜底」分割區，隨時間推進要提前把它切成
///    「新的一個月份」+「新的 p_future」，不然新資料全部塞進同一個無界分割區，
///    `DROP PARTITION` 就失去零成本清除舊資料的意義。
/// 2. 舊分割區清除——完全超出保留天數的月份分割區直接 `DROP PARTITION`（比逐筆 DELETE
///    快非常多，這也是原本設計選按月分割的理由）。
///
/// 一天跑一次就夠（不像 RollupHostedService 需要接近即時），跑在 API 專案內的
/// BackgroundService，理由跟 RollupHostedService 一致：不用 MariaDB Event Scheduler，
/// 排程邏輯留在 C# 端方便測試跟看 log。
/// </summary>
public sealed class PartitionMaintenanceHostedService(
    PartitionMaintenanceRepository partitions, ScheduledJobStatusStore jobStatus, IConfiguration configuration,
    ILogger<PartitionMaintenanceHostedService> logger) : BackgroundService
{
    public const string JobKey = "partition-maintenance";

    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(24);
    private const int MonthsAheadBuffer = 3;
    public const int DefaultChillerRetentionDays = 180;
    public const int DefaultFcuRetentionDays = 90;
    /// <summary>
    /// 縮短保留天數是不可逆的（下次排程就把超過的月份分割區整個刪掉），設定值打錯（例如 90 少打一個 0）
    /// 會一次刪掉大半歷史資料。低於這個下限一律拒絕，退回預設值。
    /// </summary>
    public const int MinRetentionDays = 30;

    private int _chillerRetentionDays = DefaultChillerRetentionDays;
    private int _fcuRetentionDays = DefaultFcuRetentionDays;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        jobStatus.Register(JobKey, "分割區維護（增補未來月份／清除過期月份）", TickInterval);
        _fcuRetentionDays = ResolveRetentionDays("Retention:FcuDays", DefaultFcuRetentionDays);
        _chillerRetentionDays = ResolveRetentionDays("Retention:ChillerDays", DefaultChillerRetentionDays);
        logger.LogInformation(
            "原始讀值保留天數：FCU {FcuDays} 天、冰水主機 {ChillerDays} 天（超過的月份分割區每日排程清除）",
            _fcuRetentionDays, _chillerRetentionDays);
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        jobStatus.Start(JobKey);
        try
        {
            var added = await EnsureFuturePartitionsAsync("chiller_reading", ct);
            added += await EnsureFuturePartitionsAsync("fcu_reading", ct);
            var dropped = await DropOldPartitionsAsync("chiller_reading", _chillerRetentionDays, ct);
            dropped += await DropOldPartitionsAsync("fcu_reading", _fcuRetentionDays, ct);
            jobStatus.Succeed(JobKey, $"新增 {added} 個分割區、清除 {dropped} 個過期分割區");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "分割區維護排程失敗，將於下次排程時間自動重試");
            jobStatus.Fail(JobKey, ex);
        }
    }

    /// <summary>
    /// 讀 `.env` 經 compose 傳進來的保留天數：留空用預設值；不是整數或低於 <see cref="MinRetentionDays"/>
    /// 一律退回預設值並寫警告，不照著執行——寧可保留多一點，也不要因為打錯字刪掉歷史資料。
    /// </summary>
    private int ResolveRetentionDays(string key, int defaultDays)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return defaultDays;
        if (int.TryParse(raw, out var days) && days >= MinRetentionDays) return days;
        logger.LogWarning(
            "設定 {Key}={Raw} 無效（必須是 {Min} 以上的整數），改用預設值 {Default} 天", key, raw, MinRetentionDays, defaultDays);
        return defaultDays;
    }

    /// <summary>解析 `p_YYYY_MM` 型式的分割區名稱，抓不到就回傳 false（`p_future`／`p_before_*` 都不符合這個格式）。</summary>
    private static bool TryParseMonth(string partitionName, out DateTime monthStartUtc)
    {
        monthStartUtc = default;
        var rest = partitionName["p_".Length..];
        var parts = rest.Split('_');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m) || m is < 1 or > 12)
            return false;
        monthStartUtc = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }

    /// <summary>這個分割區涵蓋到哪一天之前（不含）；`p_future` 回傳 null（永遠不清）。</summary>
    private static DateTime? PartitionEndUtc(string partitionName)
    {
        if (partitionName == "p_future") return null;
        if (TryParseMonth(partitionName, out var monthStart)) return monthStart.AddMonths(1);
        // 「p_before_YYYY_MM」這種初始 catch-all 分割區：名稱本身就是它的上界。
        if (partitionName.StartsWith("p_before_") && TryParseMonth("p_" + partitionName["p_before_".Length..], out var boundary))
            return boundary;
        return null;
    }

    private async Task<int> EnsureFuturePartitionsAsync(string table, CancellationToken ct)
    {
        var existing = (await partitions.ListPartitionsAsync(ct)).Where(p => p.TableName == table).ToList();
        var namedMonths = existing
            .Select(p => p.PartitionName)
            .Where(n => TryParseMonth(n, out _))
            .Select(n => { TryParseMonth(n, out var month); return month; })
            .ToList();

        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var latestExisting = namedMonths.Count > 0 ? namedMonths.Max() : thisMonth.AddMonths(-1);
        var target = thisMonth.AddMonths(MonthsAheadBuffer);

        var added = 0;
        for (var cursor = latestExisting.AddMonths(1); cursor <= target; cursor = cursor.AddMonths(1))
        {
            await partitions.AddMonthlyPartitionAsync(table, cursor, ct);
            logger.LogInformation("分割區增補：{Table} 新增 p_{Month:yyyy_MM}", table, cursor);
            added++;
        }
        return added;
    }

    private async Task<int> DropOldPartitionsAsync(string table, int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-retentionDays);
        var existing = (await partitions.ListPartitionsAsync(ct)).Where(p => p.TableName == table).ToList();
        var remaining = existing.Count;
        var dropped = 0;

        foreach (var p in existing)
        {
            if (remaining <= 1) break; // 保底：至少留一個分割區，不能全刪光
            var end = PartitionEndUtc(p.PartitionName);
            if (end is null || end.Value > cutoff) continue;

            await partitions.DropPartitionAsync(table, p.PartitionName, ct);
            remaining--;
            dropped++;
            logger.LogInformation(
                "分割區清除：{Table}.{Partition}（涵蓋到 {End:yyyy-MM-dd} 之前，超過 {RetentionDays} 天保留期）",
                table, p.PartitionName, end, retentionDays);
        }
        return dropped;
    }
}
