using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>class＋init 屬性，不用 positional record——理由見 MembershipRepository.MerchantUserRow 上的註解。</summary>
public sealed class ChillerReadingRow
{
    public DateTime Ts { get; init; }
    public decimal? ChilledWaterOut { get; init; }
    public decimal? ChilledWaterIn { get; init; }
    public decimal? ChilledWaterDelta { get; init; }
    public decimal? InputPowerKw { get; init; }
    public decimal? AccumulatedKwh { get; init; }
    public int? LoadPercentage { get; init; }
    public int ReadStatus { get; init; }
    /// <summary>只有 interval="1h" 時才有值——單調遞增計數器，rollup 存的是該小時最後一筆讀值。</summary>
    public int? RunningHours { get; init; }
}

public sealed class ChillerRepository(TecoDbConnectionFactory factory)
{
    public async Task<IReadOnlyList<DeviceChiller>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<ChillerRow>(
            "SELECT id, code, modbus_id AS ModbusId, display_name AS DisplayName, " +
            "rated_capacity_rt AS RatedCapacityRt, is_active AS IsActive FROM device_chiller WHERE is_active = 1 ORDER BY modbus_id");

        return rows.Select(r => new DeviceChiller
        {
            Id = r.Id, Code = r.Code, ModbusId = r.ModbusId, DisplayName = r.DisplayName,
            RatedCapacityRt = r.RatedCapacityRt, IsActive = r.IsActive,
        }).ToList();
    }

    /// <summary>
    /// 只更新場館自己維護的顯示名稱／自訂代碼，不動 code/modbus_id——那兩個是對應實體設備的
    /// 識別碼（modbus_id 要跟 Collector 的快照鍵值對得上），後台不該改。傳 null 代表清空。
    /// </summary>
    public async Task UpdateDisplayNameAsync(int deviceId, string? displayName, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE device_chiller SET display_name = @displayName WHERE id = @deviceId",
            new { deviceId, displayName });
    }

    /// <summary>
    /// interval="raw" 直接查 chiller_reading（注意：只有節流後落地的頻率，不是原始 5 秒）；
    /// interval="1h" 查 rollup_chiller_1h。時間範圍皆為 UTC。
    /// </summary>
    public async Task<IReadOnlyList<ChillerReadingRow>> GetHistoryAsync(
        int deviceId, DateTime fromUtc, DateTime toUtc, string interval, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);

        if (interval == "1h")
        {
            var rows = await conn.QueryAsync<ChillerReadingRow>(
                "SELECT bucket AS Ts, min_chilled_out AS ChilledWaterOut, min_chilled_in AS ChilledWaterIn, " +
                "avg_chilled_delta AS ChilledWaterDelta, avg_power_kw AS InputPowerKw, kwh_delta AS AccumulatedKwh, " +
                "avg_load_pct AS LoadPercentage, running_hours AS RunningHours, 1 AS ReadStatus " +
                "FROM rollup_chiller_1h WHERE device_id = @deviceId AND bucket BETWEEN @fromUtc AND @toUtc ORDER BY bucket",
                new { deviceId, fromUtc, toUtc });
            return rows.ToList();
        }

        var raw = await conn.QueryAsync<ChillerReadingRow>(
            "SELECT ts AS Ts, chilled_water_out AS ChilledWaterOut, chilled_water_in AS ChilledWaterIn, " +
            "chilled_water_delta AS ChilledWaterDelta, " +
            "input_power_kw AS InputPowerKw, accumulated_kwh AS AccumulatedKwh, load_percentage AS LoadPercentage, " +
            "running_hours AS RunningHours, read_status AS ReadStatus " +
            "FROM chiller_reading WHERE device_id = @deviceId AND ts BETWEEN @fromUtc AND @toUtc ORDER BY ts",
            new { deviceId, fromUtc, toUtc });
        return raw.ToList();
    }

    /// <summary>
    /// 把 [fromUtc, toUtcExclusive) 這段時間內的原始讀值，依 UTC 整點分桶聚合寫進
    /// rollup_chiller_1h（INSERT...SELECT...GROUP BY 一次做完，不逐筆搬到 C# 端算——
    /// 資料量可能上萬筆，SQL 端聚合快得多）。
    ///
    /// 只吃 read_status = Success 的列：2026-09-29 以前 Collector 在斷線/讀取失敗時也會寫一筆
    /// （欄位值是 SDK 殘值，不是 NULL），現在已改成不寫，但舊資料還留在表裡，過濾不能拿掉——
    /// 拿殘值算平均會讓整小時的統計失真，比顯示「這小時沒資料」更誤導人。
    ///
    /// bucket 全程是 UTC 整點，沒有跨時區換算問題，直接在 SQL 端用 DATE_FORMAT 截斷即可，
    /// 不用比照本地日曆日聚合那樣搬到 C# 用 TimeZoneInfo 做。
    /// </summary>
    public async Task UpsertHourlyRollupAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            """
            INSERT INTO rollup_chiller_1h
                (device_id, bucket, avg_load_pct, min_chilled_out, max_chilled_out,
                 min_chilled_in, max_chilled_in, avg_chilled_delta, avg_power_kw, kwh_delta,
                 running_hours, run_minutes)
            SELECT
                device_id,
                DATE_FORMAT(ts, '%Y-%m-%d %H:00:00') AS bucket,
                AVG(load_percentage),
                MIN(chilled_water_out), MAX(chilled_water_out),
                MIN(chilled_water_in), MAX(chilled_water_in),
                AVG(chilled_water_delta),
                AVG(input_power_kw),
                MAX(accumulated_kwh) - MIN(accumulated_kwh),
                MAX(running_hours),
                COUNT(DISTINCT CASE WHEN load_percentage > 0 THEN DATE_FORMAT(ts, '%Y-%m-%d %H:%i') END)
            FROM chiller_reading
            WHERE read_status = 1 AND ts >= @fromUtc AND ts < @toUtcExclusive
            GROUP BY device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00')
            ON DUPLICATE KEY UPDATE
                avg_load_pct = VALUES(avg_load_pct),
                min_chilled_out = VALUES(min_chilled_out), max_chilled_out = VALUES(max_chilled_out),
                min_chilled_in = VALUES(min_chilled_in), max_chilled_in = VALUES(max_chilled_in),
                avg_chilled_delta = VALUES(avg_chilled_delta),
                avg_power_kw = VALUES(avg_power_kw), kwh_delta = VALUES(kwh_delta),
                running_hours = VALUES(running_hours), run_minutes = VALUES(run_minutes)
            """,
            new { fromUtc, toUtcExclusive }, commandTimeout: 60);
    }

    private sealed class ChillerRow
    {
        public int Id { get; init; }
        public required string Code { get; init; }
        public int ModbusId { get; init; }
        public required string DisplayName { get; init; }
        public decimal? RatedCapacityRt { get; init; }
        public bool IsActive { get; init; }
    }
}
