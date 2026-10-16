using Dapper;
using Teco.Hvac.Contracts;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 通道健康度與連線區段歷史資料庫倉儲 (<see cref="ChannelHealthRepository"/>)。
/// 對應資料庫 channel_health 資料表。
/// 
/// 採區段 (Segment) 設計模型，記錄通訊通道每次連線或斷線的開始時間，並於該區段結束時回填 duration_seconds（持續秒數）。
/// </summary>
public sealed class ChannelHealthRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 查詢指定通道最近一筆連線狀態所使用的輕量 Row 物件。
    /// </summary>
    public sealed class LatestRow
    {
        /// <summary>紀錄主鍵 ID。</summary>
        public long Id { get; init; }
        /// <summary>連線狀態整數值 (ConnectionState)。</summary>
        public int ConnectionState { get; init; }
        /// <summary>狀態變更發生的標準 UTC 時間（使用 DateTime 宣告避免轉型時區污染）。</summary>
        public DateTime ChangedAt { get; init; }
    }

    /// <summary>
    /// 查詢指定通道最新一筆連線歷史紀錄（用於 Collector 重啟時接續歷史區段）。
    /// </summary>
    /// <param name="channel">目標通訊通道。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>該通道最後一筆紀錄；若資料庫尚無紀錄則為 null。</returns>
    public async Task<LatestRow?> GetLatestAsync(Channel channel, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<LatestRow>(
            "SELECT id AS Id, connection_state AS ConnectionState, changed_at AS ChangedAt " +
            "FROM channel_health WHERE channel = @channel ORDER BY changed_at DESC, id DESC LIMIT 1",
            new { channel = (int)channel });
    }

    /// <summary>
    /// 開立全新的通道狀態區段：在交易內回填上一區段的持續時間 (DurationSeconds)，並插入新區段列。
    /// </summary>
    /// <param name="channel">通訊通道。</param>
    /// <param name="state">新區段之連線狀態。</param>
    /// <param name="readStatus">新區段之資料讀取狀態。</param>
    /// <param name="changedAtUtc">狀態變更發生的標準 UTC 時間戳記。</param>
    /// <param name="previousId">上一區段的資料表主鍵 ID；若為首筆紀錄則為 null。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>新開立區段之自動遞增主鍵 ID。</returns>
    public async Task<long> StartSegmentAsync(
        Channel channel, ConnectionState state, ReadStatus readStatus, DateTimeOffset changedAtUtc,
        long? previousId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        using var tx = await conn.BeginTransactionAsync(ct);
        var changedAt = changedAtUtc.UtcDateTime;
        if (previousId is not null)
        {
            // 使用 GREATEST(0, ...) 防止因 NTP 伺服器校時或時間回退導致算出的持續秒數為負數
            await conn.ExecuteAsync(
                "UPDATE channel_health SET duration_seconds = GREATEST(0, TIMESTAMPDIFF(SECOND, changed_at, @changedAt)) " +
                "WHERE id = @previousId",
                new { previousId, changedAt }, tx);
        }
        var id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO channel_health (channel, connection_state, read_status, changed_at) " +
            "VALUES (@channel, @state, @readStatus, @changedAt); SELECT LAST_INSERT_ID();",
            new { channel = (int)channel, state = (int)state, readStatus = (int)readStatus, changedAt }, tx);
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>
    /// 依據截止時間批次清除歷史過期健康狀態紀錄。
    /// 採用分批 LIMIT 10000 刪除策略，避免單次大交易鎖表影響線上讀寫。
    /// </summary>
    /// <param name="cutoffUtc">過期資料刪除截止時間（早於此時間點的紀錄將被清除）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>累計已刪除的資料總筆數。</returns>
    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var channels = Enum.GetValues<Channel>().Select(c => (int)c).ToArray();
        var total = 0;
        while (true)
        {
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM channel_health WHERE channel IN @channels AND changed_at < @cutoffUtc LIMIT 10000",
                new { channels, cutoffUtc }, commandTimeout: 120);
            total += deleted;
            if (deleted < 10000) return total;
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// 前端系統診斷與通訊歷史查詢專用 Row 物件。
    /// </summary>
    public sealed class ChannelHealthRow
    {
        /// <summary>紀錄主鍵 ID。</summary>
        public long Id { get; init; }
        /// <summary>通道列舉數值。</summary>
        public int Channel { get; init; }
        /// <summary>連線狀態列舉數值。</summary>
        public int ConnectionState { get; init; }
        /// <summary>讀取狀態列舉數值。</summary>
        public int ReadStatus { get; init; }
        /// <summary>變更時間（UTC DateTime，由呼叫端透過 AsUtcOffset() 轉換為 DateTimeOffset）。</summary>
        public DateTime ChangedAt { get; init; }
        /// <summary>該狀態持續歷時（秒數）；若進行中則為 null。</summary>
        public int? DurationSeconds { get; init; }
    }

    /// <summary>
    /// 查詢全廠通道最近的健康狀態與連線歷程紀錄。
    /// 供平台維運「系統診斷」頁面呈現通道離線與跳動軌跡。
    /// </summary>
    /// <param name="limit">查詢最大回傳筆數。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>通道健康歷史 Row 清單。</returns>
    public async Task<IReadOnlyList<ChannelHealthRow>> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<ChannelHealthRow>(
            "SELECT id AS Id, channel AS Channel, connection_state AS ConnectionState, read_status AS ReadStatus, " +
            "changed_at AS ChangedAt, duration_seconds AS DurationSeconds " +
            "FROM channel_health ORDER BY changed_at DESC, id DESC LIMIT @limit",
            new { limit });
        return rows.ToList();
    }
}
