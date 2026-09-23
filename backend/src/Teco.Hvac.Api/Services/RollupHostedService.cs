using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Services;

/// <summary>
/// 每小時聚合排程（見 docs/BACKEND_INTEGRATION_PLAN.md §6.2/§6.3）。跑在 API 專案內，
/// 不用 MariaDB Event Scheduler——比照本專案「時區/排程邏輯在 C# 做」的既有原則。
///
/// 設計成滾動視窗，不是「只補一次」：每次 tick 都重新聚合最近 <see cref="RollingWindow"/>
/// 這段時間，用 ON DUPLICATE KEY UPDATE 覆寫，這樣即使某次 tick 因為重啟／資料庫短暫不可用
/// 而跳過，下一次 tick 還是會自動補回來，不需要另外做「補漏」邏輯或記錄「上次跑到哪」的狀態。
///
/// 只保證「最近一段時間」的資料會持續補齊；更久以前、這個排程還沒上線之前的歷史資料不會
/// 自動回補，需要的話要另外手動對 ChillerRepository/FcuRepository.UpsertHourlyRollupAsync
/// 跑一次更大範圍的區間（見 backend/README.md 的已知限制）。
/// </summary>
public sealed class RollupHostedService(
    ChillerRepository chillers, FcuRepository fcus, ILogger<RollupHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RollingWindow = TimeSpan.FromHours(26);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 啟動先跑一次，不用等第一個 15 分鐘的 tick。
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var nowUtc = DateTime.UtcNow;
            // 只聚合「已經完整結束」的整點，避免把還在進行中的這個小時算成不完整的統計。
            var toUtcExclusive = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
            var fromUtc = toUtcExclusive - RollingWindow;

            await chillers.UpsertHourlyRollupAsync(fromUtc, toUtcExclusive, ct);
            await fcus.UpsertHourlyRollupAsync(fromUtc, toUtcExclusive, ct);

            logger.LogInformation("每小時聚合排程完成：{From:O} ~ {To:O}", fromUtc, toUtcExclusive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 跟 Collector 的規則熱重載一樣的態度：這次失敗不影響下次 tick，沿用滾動視窗
            // 下次自然會重跑，不需要在這裡重試。
            logger.LogWarning(ex, "每小時聚合排程失敗，將於下次排程時間自動重試");
        }
    }
}
