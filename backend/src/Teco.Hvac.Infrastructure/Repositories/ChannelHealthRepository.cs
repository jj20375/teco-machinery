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
}
