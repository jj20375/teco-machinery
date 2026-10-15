using Dapper;
using Teco.Hvac.Contracts;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 通道連線狀態「區段」紀錄，由 Collector 看門狗寫入（計畫 P2）。每一列是一段持續的狀態，
/// `duration_seconds` 在下一段開始時才回填；NULL 代表這段還在持續（或 2026-10-15 之前的舊資料，當時沒有回填）。
/// </summary>
public sealed class ChannelHealthRepository(TecoDbConnectionFactory factory)
{
    public sealed class LatestRow
    {
        public long Id { get; init; }
        public int ConnectionState { get; init; }
        /// <summary>UTC；刻意宣告成 DateTime（見 CLAUDE.md 原則 1）。</summary>
        public DateTime ChangedAt { get; init; }
    }

    public async Task<LatestRow?> GetLatestAsync(Channel channel, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<LatestRow>(
            "SELECT id AS Id, connection_state AS ConnectionState, changed_at AS ChangedAt " +
            "FROM channel_health WHERE channel = @channel ORDER BY changed_at DESC, id DESC LIMIT 1",
            new { channel = (int)channel });
    }

    /// <summary>開始新的一段狀態：回填上一段的持續秒數，並寫入新的一列。回傳新列的 id。</summary>
    public async Task<long> StartSegmentAsync(
        Channel channel, ConnectionState state, ReadStatus readStatus, DateTimeOffset changedAtUtc,
        long? previousId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        using var tx = await conn.BeginTransactionAsync(ct);
        var changedAt = changedAtUtc.UtcDateTime;
        if (previousId is not null)
        {
            // GREATEST：DLL 的事件時間是本機時間，主機校時可能讓新事件早於上一段，不要寫出負數。
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
    /// 刪除早於 cutoff 的紀錄，回傳刪除筆數。分批刪，避免一次鎖住大量資料列
    /// （修正前的版本在設備長期連不上時一天會寫好幾萬筆，舊資料庫可能還留著那批量）。
    /// `channel IN (...)` 是為了用到 (channel, changed_at) 索引做範圍掃描。
    /// </summary>
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

    public sealed class ChannelHealthRow
    {
        public long Id { get; init; }
        public int Channel { get; init; }
        public int ConnectionState { get; init; }
        public int ReadStatus { get; init; }
        /// <summary>UTC；刻意宣告成 DateTime，由呼叫端 AsUtcOffset() 轉換（見 CLAUDE.md 原則 1）。</summary>
        public DateTime ChangedAt { get; init; }
        public int? DurationSeconds { get; init; }
    }

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
