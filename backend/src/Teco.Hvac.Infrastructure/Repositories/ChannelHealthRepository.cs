using Dapper;
using Teco.Hvac.Contracts;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>通道健康狀態變化紀錄，由 Collector 看門狗寫入（計畫 P2）。</summary>
public sealed class ChannelHealthRepository(TecoDbConnectionFactory factory)
{
    public async Task RecordAsync(
        Channel channel, ConnectionState state, ReadStatus readStatus, DateTimeOffset changedAtUtc,
        CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO channel_health (channel, connection_state, read_status, changed_at) " +
            "VALUES (@channel, @state, @readStatus, @changedAt)",
            new { channel = (int)channel, state = (int)state, readStatus = (int)readStatus, changedAt = changedAtUtc.UtcDateTime });
    }

    public sealed class ChannelHealthRow
    {
        public long Id { get; init; }
        public int Channel { get; init; }
        public int ConnectionState { get; init; }
        public int ReadStatus { get; init; }
        /// <summary>UTC；刻意宣告成 DateTime，由呼叫端 AsUtcOffset() 轉換（見 CLAUDE.md 原則 1）。</summary>
        public DateTime ChangedAt { get; init; }
    }

    public async Task<IReadOnlyList<ChannelHealthRow>> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<ChannelHealthRow>(
            "SELECT id AS Id, channel AS Channel, connection_state AS ConnectionState, read_status AS ReadStatus, " +
            "changed_at AS ChangedAt FROM channel_health ORDER BY changed_at DESC, id DESC LIMIT @limit",
            new { limit });
        return rows.ToList();
    }
}
