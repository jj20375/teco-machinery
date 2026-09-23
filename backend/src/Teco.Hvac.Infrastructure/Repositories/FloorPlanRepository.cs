using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>class＋init 屬性，不用 positional record——理由見 MembershipRepository.MerchantUserRow 上的註解。</summary>
public sealed class FloorPlacementRow
{
    public int DeviceType { get; init; }
    public int DeviceId { get; init; }
    public required string Floor { get; init; }
    public required string AreaId { get; init; }
    public decimal X { get; init; }
    public decimal Y { get; init; }
    public int Rotation { get; init; }
    public int? UpdatedBy { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class FloorPlanRepository(TecoDbConnectionFactory factory)
{
    public async Task<IReadOnlyList<DeviceFloorPlacement>> ListByFloorAsync(string floor, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<FloorPlacementRow>(
            """
            SELECT device_type AS DeviceType, device_id AS DeviceId, floor AS Floor, area_id AS AreaId,
                   x AS X, y AS Y, rotation AS Rotation, updated_by AS UpdatedBy, updated_at AS UpdatedAt
            FROM device_floor_placement WHERE floor = @floor
            ORDER BY device_type, device_id
            """, new { floor });

        return rows.Select(r => new DeviceFloorPlacement
        {
            DeviceType = (AlarmDeviceType)r.DeviceType,
            DeviceId = r.DeviceId,
            Floor = r.Floor,
            AreaId = r.AreaId,
            X = r.X,
            Y = r.Y,
            Rotation = r.Rotation,
            UpdatedByUserId = r.UpdatedBy,
            UpdatedAtUtc = new DateTimeOffset(r.UpdatedAt, TimeSpan.Zero),
        }).ToList();
    }

    /// <summary>
    /// 整層的版本依據：該樓層所有配置裡最新的 updated_at。沒有任何配置時回 null
    /// （代表這層還沒被配置過，樂觀鎖的期望值也應該是 null）。
    /// </summary>
    public async Task<DateTimeOffset?> GetFloorVersionAsync(string floor, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var max = await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT MAX(updated_at) FROM device_floor_placement WHERE floor = @floor", new { floor });
        return max is null ? null : new DateTimeOffset(max.Value, TimeSpan.Zero);
    }

    /// <summary>
    /// 整層覆寫：先刪掉這層舊配置再寫入新的，全程在同一個交易裡，避免刪完還沒寫完就中斷
    /// 導致整層配置消失。回傳寫入後的新版本時間。
    /// </summary>
    public async Task<DateTimeOffset> ReplaceFloorAsync(
        string floor, IReadOnlyCollection<DeviceFloorPlacement> placements, int updatedByUserId, CancellationToken ct = default)
    {
        // 截到毫秒：欄位是 DATETIME(3)，若回傳微秒精度的時間，呼叫端拿它當 expectedVersion
        // 再存一次時就會跟資料庫裡的值對不起來。
        var nowUtc = new DateTimeOffset(
            DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        using var conn = await factory.CreateOpenAsync(ct);
        using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("DELETE FROM device_floor_placement WHERE floor = @floor", new { floor }, tx);

        if (placements.Count > 0)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO device_floor_placement
                    (device_type, device_id, floor, area_id, x, y, rotation, updated_by, updated_at)
                VALUES
                    (@DeviceType, @DeviceId, @Floor, @AreaId, @X, @Y, @Rotation, @UpdatedBy, @UpdatedAt)
                """,
                placements.Select(p => new
                {
                    DeviceType = (int)p.DeviceType,
                    p.DeviceId,
                    Floor = floor,
                    p.AreaId,
                    p.X,
                    p.Y,
                    p.Rotation,
                    UpdatedBy = updatedByUserId,
                    UpdatedAt = nowUtc.UtcDateTime,
                }), tx);
        }

        await tx.CommitAsync(ct);
        return nowUtc;
    }

    /// <summary>
    /// 把 FCU 的所在分區同步回 device_fcu.zone_code——那個欄位原本永遠是 NULL，同步之後
    /// 報表與熱區圖才能用分區統計。設備被移出圖面時一併清成 NULL。
    /// </summary>
    public async Task SyncFcuZoneCodesAsync(
        string floor, IReadOnlyCollection<DeviceFloorPlacement> placements, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE device_fcu SET zone_code = NULL WHERE floor = @floor", new { floor });

        var fcuPlacements = placements.Where(p => p.DeviceType == AlarmDeviceType.Fcu).ToList();
        if (fcuPlacements.Count == 0) return;

        await conn.ExecuteAsync(
            "UPDATE device_fcu SET zone_code = @AreaId WHERE id = @DeviceId",
            fcuPlacements.Select(p => new { p.AreaId, p.DeviceId }));
    }
}
