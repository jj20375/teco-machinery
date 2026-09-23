using Dapper;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>class＋init 屬性，不用 positional record——理由見 MembershipRepository.MerchantUserRow 上的註解。</summary>
public sealed class FcuReadingRow
{
    public DateTime Ts { get; init; }
    public int? SwitchStatus { get; init; }
    public int? Mode { get; init; }
    public int? FanSpeed { get; init; }
    public decimal? Temperature { get; init; }
    public int ReadStatus { get; init; }
    /// <summary>只有 interval="1h"（查 rollup_fcu_1h）時才有值；raw 模式固定是 null。</summary>
    public int? OnMinutes { get; init; }
}

/// <summary>給前台每小時趨勢圖用的原始列——只挑聚合需要的欄位，JOIN device_fcu 拿 floor。</summary>
public sealed class FcuHourlyReadingRow
{
    public int DeviceId { get; init; }
    public DateTime Ts { get; init; }
    public int? SwitchStatus { get; init; }
    public decimal? Temperature { get; init; }
    public int ReadStatus { get; init; }
    public required string Floor { get; init; }
}

public sealed class FcuRepository(TecoDbConnectionFactory factory)
{
    public async Task<IReadOnlyList<DeviceFcu>> ListAsync(string? floor = null, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var sql = "SELECT id, channel AS Channel, station_id AS StationId, position AS Position, " +
                   "address AS Address, floor AS Floor, zone_code AS ZoneCode, display_name AS DisplayName, " +
                   "is_active AS IsActive FROM device_fcu WHERE is_active = 1";
        if (!string.IsNullOrWhiteSpace(floor)) sql += " AND floor = @floor";
        sql += " ORDER BY channel, station_id, position";

        var rows = await conn.QueryAsync<FcuRow>(sql, new { floor });
        return rows.Select(r => new DeviceFcu
        {
            Id = r.Id, Channel = (Channel)r.Channel, StationId = (byte)r.StationId, Position = r.Position,
            Address = (ushort)r.Address, Floor = r.Floor, ZoneCode = r.ZoneCode, DisplayName = r.DisplayName,
            IsActive = r.IsActive,
        }).ToList();
    }

    public async Task<DeviceFcu?> FindAsync(int deviceId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var r = await conn.QuerySingleOrDefaultAsync<FcuRow>(
            "SELECT id, channel AS Channel, station_id AS StationId, position AS Position, " +
            "address AS Address, floor AS Floor, zone_code AS ZoneCode, display_name AS DisplayName, " +
            "is_active AS IsActive FROM device_fcu WHERE id = @deviceId", new { deviceId });
        if (r is null) return null;
        return new DeviceFcu
        {
            Id = r.Id, Channel = (Channel)r.Channel, StationId = (byte)r.StationId, Position = r.Position,
            Address = (ushort)r.Address, Floor = r.Floor, ZoneCode = r.ZoneCode, DisplayName = r.DisplayName,
            IsActive = r.IsActive,
        };
    }

    /// <summary>
    /// 只更新場館自己維護的顯示名稱／自訂代碼，不動 channel/station_id/position/address 這些
    /// 實體位址欄位——那些是現場接線決定的，後台不該改。傳 null 代表清空回「未設定」。
    /// </summary>
    public async Task UpdateDisplayNameAsync(int deviceId, string? displayName, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE device_fcu SET display_name = @displayName WHERE id = @deviceId",
            new { deviceId, displayName });
    }

    /// <summary>
    /// interval="raw" 直接查 fcu_reading；interval="1h" 查 rollup_fcu_1h（比照
    /// ChillerRepository.GetHistoryAsync 的寫法）。rollup 沒有 mode/fan_speed 的聚合欄位
    /// （報表用不到、聚合也沒有明確意義），這兩個欄位在 1h 模式下固定回 null。
    /// </summary>
    public async Task<IReadOnlyList<FcuReadingRow>> GetHistoryAsync(
        int deviceId, DateTime fromUtc, DateTime toUtc, string interval = "raw", CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);

        if (interval == "1h")
        {
            var rollupRows = await conn.QueryAsync<FcuReadingRow>(
                "SELECT bucket AS Ts, NULL AS SwitchStatus, NULL AS Mode, NULL AS FanSpeed, " +
                "avg_temp AS Temperature, 1 AS ReadStatus, on_minutes AS OnMinutes " +
                "FROM rollup_fcu_1h WHERE device_id = @deviceId AND bucket BETWEEN @fromUtc AND @toUtc ORDER BY bucket",
                new { deviceId, fromUtc, toUtc });
            return rollupRows.ToList();
        }

        var rows = await conn.QueryAsync<FcuReadingRow>(
            "SELECT ts AS Ts, switch_status AS SwitchStatus, mode AS Mode, fan_speed AS FanSpeed, " +
            "temperature AS Temperature, read_status AS ReadStatus FROM fcu_reading " +
            "WHERE device_id = @deviceId AND ts BETWEEN @fromUtc AND @toUtc ORDER BY ts",
            new { deviceId, fromUtc, toUtc });
        return rows.ToList();
    }

    /// <summary>
    /// 把 [fromUtc, toUtcExclusive) 這段時間內的原始讀值，依 UTC 整點分桶聚合寫進
    /// rollup_fcu_1h，理由跟作法比照 ChillerRepository.UpsertHourlyRollupAsync
    /// （只吃 read_status=Success、bucket 是 UTC 整點不用時區換算）。
    /// </summary>
    public async Task UpsertHourlyRollupAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            """
            INSERT INTO rollup_fcu_1h (device_id, bucket, avg_temp, min_temp, max_temp, on_minutes)
            SELECT
                device_id,
                DATE_FORMAT(ts, '%Y-%m-%d %H:00:00') AS bucket,
                AVG(temperature), MIN(temperature), MAX(temperature),
                COUNT(DISTINCT CASE WHEN switch_status = 1 THEN DATE_FORMAT(ts, '%Y-%m-%d %H:%i') END)
            FROM fcu_reading
            WHERE read_status = 1 AND ts >= @fromUtc AND ts < @toUtcExclusive
            GROUP BY device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00')
            ON DUPLICATE KEY UPDATE
                avg_temp = VALUES(avg_temp), min_temp = VALUES(min_temp), max_temp = VALUES(max_temp),
                on_minutes = VALUES(on_minutes)
            """,
            new { fromUtc, toUtcExclusive }, commandTimeout: 60);
    }

    /// <summary>
    /// 給每小時趨勢圖用：撈出區間內全部 FCU 的讀值（含 floor），小時分桶跟時區換算留給
    /// 呼叫端在 C# 做——MariaDB 這個容器的 time_zone 是 SYSTEM／沒載入具名時區表，
    /// CONVERT_TZ 不可靠；一天全部 FCU 大約 13~14 萬筆，交給 C# 聚合效能上完全沒問題。
    /// </summary>
    public async Task<IReadOnlyList<FcuHourlyReadingRow>> GetReadingsForHourlyStatsAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<FcuHourlyReadingRow>(
            """
            SELECT fr.device_id AS DeviceId, fr.ts AS Ts, fr.switch_status AS SwitchStatus,
                   fr.temperature AS Temperature, fr.read_status AS ReadStatus, df.floor AS Floor
            FROM fcu_reading fr
            JOIN device_fcu df ON df.id = fr.device_id
            WHERE fr.ts BETWEEN @fromUtc AND @toUtc
            """, new { fromUtc, toUtc });
        return rows.ToList();
    }

    private sealed class FcuRow
    {
        public int Id { get; init; }
        public int Channel { get; init; }
        public int StationId { get; init; }
        public int Position { get; init; }
        public int Address { get; init; }
        public required string Floor { get; init; }
        public string? ZoneCode { get; init; }
        public string? DisplayName { get; init; }
        public bool IsActive { get; init; }
    }
}
