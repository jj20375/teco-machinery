using Dapper;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// `chiller_reading`/`fcu_reading` 是按月 RANGE 分割的時序表（見 001_schema.sql），
/// 分割區增補（在 `p_future` 用完前先切出下個月）跟舊分割區清除（`DROP PARTITION` 做
/// 零成本清除，比 `DELETE` 快非常多）都要有排程維護，不然 `p_future` 會變成單一無界分割區，
/// 失去分割表原本「清除舊資料」的意義。
///
/// 這裡的 ALTER TABLE 是字串組合，不是 Dapper 參數化查詢——這是 CLAUDE.md 原則 1 說的
/// 「真正的原生 SQL 僅限 migration DDL」的同類例外：分割區名稱／日期都是伺服器端用
/// `DateTime.UtcNow` 算出來的，不是使用者輸入，沒有 SQL injection 風險，而 DDL 語法本身
/// 也不支援參數化。
/// </summary>
public sealed class PartitionMaintenanceRepository(TecoDbConnectionFactory factory)
{
    private static readonly string[] ManagedTables = ["chiller_reading", "fcu_reading"];

    public sealed class PartitionInfo
    {
        public required string TableName { get; init; }
        public required string PartitionName { get; init; }
        /// <summary>MAXVALUE（`p_future`）時是 null；否則是 `TO_DAYS()` 的分割上界。</summary>
        public long? UpperBoundDays { get; init; }
    }

    public async Task<IReadOnlyList<PartitionInfo>> ListPartitionsAsync(CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<(string TableName, string PartitionName, string Description)>(
            """
            SELECT table_name AS TableName, partition_name AS PartitionName, partition_description AS Description
            FROM information_schema.PARTITIONS
            WHERE table_schema = DATABASE() AND table_name IN @tables AND partition_name IS NOT NULL
            ORDER BY table_name, partition_ordinal_position
            """,
            new { tables = ManagedTables });

        return rows.Select(r => new PartitionInfo
        {
            TableName = r.TableName,
            PartitionName = r.PartitionName,
            UpperBoundDays = r.Description == "MAXVALUE" ? null : long.Parse(r.Description),
        }).ToList();
    }

    /// <summary>把 `p_future` 切成「新的一個月份分割區」+ 新的 `p_future`。</summary>
    public async Task AddMonthlyPartitionAsync(string tableName, DateTime monthStartUtc, CancellationToken ct = default)
    {
        var partitionName = $"p_{monthStartUtc:yyyy_MM}";
        var nextMonthStart = monthStartUtc.AddMonths(1);
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            $"""
            ALTER TABLE {tableName} REORGANIZE PARTITION p_future INTO (
                PARTITION {partitionName} VALUES LESS THAN (TO_DAYS('{nextMonthStart:yyyy-MM-dd}')),
                PARTITION p_future VALUES LESS THAN MAXVALUE
            )
            """, commandTimeout: 120);
    }

    public async Task DropPartitionAsync(string tableName, string partitionName, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync($"ALTER TABLE {tableName} DROP PARTITION {partitionName}", commandTimeout: 120);
    }
}
