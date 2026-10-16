using Dapper;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 資料庫時序表分割區 (Table Partitions) 維護倉儲 (<see cref="PartitionMaintenanceRepository"/>)。
/// 
/// 【時序分割架構】
/// chiller_reading 與 fcu_reading 採用按月 RANGE COLUMNS/TO_DAYS 分割設計。
/// 本倉儲由背景排程 HostedService 定期調用：
/// 1. 分割區增補：在每月結束前自動將 p_future 分割區重構 (REORGANIZE PARTITION) 切出下月份專屬分割區，防止時序資料全數混入單一無界分區。
/// 2. 過期分割區刪除：透過 DROP PARTITION 進行近乎零成本的底層資料快速清除，速度遠高於傳統 DELETE 且不產生表鎖碎片。
/// </summary>
public sealed class PartitionMaintenanceRepository(TecoDbConnectionFactory factory)
{
    /// <summary>由分割區維護服務納管的時序資料表名稱清單。</summary>
    private static readonly string[] ManagedTables = ["chiller_reading", "fcu_reading"];

    /// <summary>
    /// 資料表分割區資訊物件 (<see cref="PartitionInfo"/>)。
    /// </summary>
    public sealed class PartitionInfo
    {
        /// <summary>資料表名稱（例如 "chiller_reading"）。</summary>
        public required string TableName { get; init; }

        /// <summary>分割區名稱（例如 "p_2026_10"、"p_future"）。</summary>
        public required string PartitionName { get; init; }

        /// <summary>
        /// 分割區的 TO_DAYS() 上界天數數值；若為未限定上界之 MAXVALUE (p_future) 則為 null。
        /// </summary>
        public long? UpperBoundDays { get; init; }
    }

    /// <summary>
    /// 自 MySQL/MariaDB 系統目錄 information_schema.PARTITIONS 查詢所有納管資料表之現有分割區清單。
    /// </summary>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>分割區資訊集合。</returns>
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

    /// <summary>
    /// 執行 DDL 重組分割區：將 p_future 切出指定月份的新分割區並保留新的 p_future。
    /// </summary>
    /// <param name="tableName">目標資料表名稱。</param>
    /// <param name="monthStartUtc">目標月份的第一天起始時間 (UTC)。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步 DDL 執行作業的 <see cref="Task"/>。</returns>
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

    /// <summary>
    /// 執行 DDL 快速刪除指定資料表之過期分割區 (DROP PARTITION)。
    /// </summary>
    /// <param name="tableName">目標資料表名稱。</param>
    /// <param name="partitionName">欲刪除之分割區名稱（例如 "p_2025_01"）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步 DDL 執行作業的 <see cref="Task"/>。</returns>
    public async Task DropPartitionAsync(string tableName, string partitionName, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync($"ALTER TABLE {tableName} DROP PARTITION {partitionName}", commandTimeout: 120);
    }
}
