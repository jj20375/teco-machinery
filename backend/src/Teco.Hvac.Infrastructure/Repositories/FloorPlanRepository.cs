using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 平面圖設備座標配置資料庫 Row 物件 (<see cref="FloorPlacementRow"/>)。
/// 採用類別屬性配合 init 設定子，提供 Dapper 映射最佳相容性。
/// </summary>
public sealed class FloorPlacementRow
{
    /// <summary>設備類型整數值 (AlarmDeviceType)。</summary>
    public int DeviceType { get; init; }
    /// <summary>設備在資料庫中的主鍵 ID。</summary>
    public int DeviceId { get; init; }
    /// <summary>樓層代碼（例如 "B1"、"B2"）。</summary>
    public required string Floor { get; init; }
    /// <summary>平面圖分區代碼（例如 "B1-Z07"）。</summary>
    public required string AreaId { get; init; }
    /// <summary>畫布 X 座標。</summary>
    public decimal X { get; init; }
    /// <summary>畫布 Y 座標。</summary>
    public decimal Y { get; init; }
    /// <summary>圖示旋轉角度。</summary>
    public int Rotation { get; init; }
    /// <summary>更新者使用者 ID。</summary>
    public int? UpdatedBy { get; init; }
    /// <summary>更新時間 (UTC DateTime)。</summary>
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// 樓層平面圖與設備錨點座標倉儲 (<see cref="FloorPlanRepository"/>)。
/// 
/// 負責維護設備在 2D/3D 平面圖上的放置座標 (X, Y)、旋轉角度與所屬分區 (AreaId)。
/// 提供整層查詢、樂觀鎖版本控制以及整層覆寫替換功能。
/// </summary>
public sealed class FloorPlanRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 查詢指定樓層目前已配置的所有設備圖面座標清單。
    /// </summary>
    /// <param name="floor">目標樓層代碼（例如 "B1" 或 "B2"）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>該樓層所有設備擺放位置實體清單。</returns>
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
    /// 取得指定樓層當前配置的樂觀鎖版本時間（該樓層最新一筆 placement 之 updated_at）。
    /// 若該樓層尚未建立任何配置，回傳 null。
    /// </summary>
    /// <param name="floor">樓層代碼。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>最新版本時間戳記；若無資料則為 null。</returns>
    public async Task<DateTimeOffset?> GetFloorVersionAsync(string floor, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var max = await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT MAX(updated_at) FROM device_floor_placement WHERE floor = @floor", new { floor });
        return max is null ? null : new DateTimeOffset(max.Value, TimeSpan.Zero);
    }

    /// <summary>
    /// 在單一資料庫交易內以全新配置整層覆寫原有的設備座標紀錄。
    /// 先清空該樓層舊有紀錄再批次插入新座標，保證資料原子性。
    /// </summary>
    /// <param name="floor">樓層代碼。</param>
    /// <param name="placements">欲更新的設備座標清單。</param>
    /// <param name="updatedByUserId">執行儲存操作之使用者 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表本次覆寫寫入之全新版本時間戳記（截至毫秒）。</returns>
    public async Task<DateTimeOffset> ReplaceFloorAsync(
        string floor, IReadOnlyCollection<DeviceFloorPlacement> placements, int updatedByUserId, CancellationToken ct = default)
    {
        // 將時間截斷至毫秒精度，對齊資料庫 DATETIME(3) 欄位規格，避免樂觀鎖微秒誤差
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
    /// 將圖面上配置的 FCU 分區代碼 (AreaId) 反向同步更新回 device_fcu.zone_code 欄位。
    /// 供報表與熱區圖快速依分區彙整統計；若設備自圖面上移除則清為 NULL。
    /// </summary>
    /// <param name="floor">目標樓層。</param>
    /// <param name="placements">當前圖面上的設備座標集合。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步同步作業的 <see cref="Task"/>。</returns>
    public async Task SyncFcuZoneCodesAsync(
        string floor, IReadOnlyCollection<DeviceFloorPlacement> placements, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        // 先將該樓層所有 FCU 的 zone_code 重設為 NULL
        await conn.ExecuteAsync(
            "UPDATE device_fcu SET zone_code = NULL WHERE floor = @floor", new { floor });

        var fcuPlacements = placements.Where(p => p.DeviceType == AlarmDeviceType.Fcu).ToList();
        if (fcuPlacements.Count == 0) return;

        // 批次依據 DeviceId 更新最新的 AreaId
        await conn.ExecuteAsync(
            "UPDATE device_fcu SET zone_code = @AreaId WHERE id = @DeviceId",
            fcuPlacements.Select(p => new { p.AreaId, p.DeviceId }));
    }
}
