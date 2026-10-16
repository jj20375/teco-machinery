using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 冰水主機歷史讀值與報表查詢 Row 物件 (<see cref="ChillerReadingRow"/>)。
/// 採用類別屬性配合 init 設定子，支援原始資料表與小時聚合表之欄位映射。
/// </summary>
public sealed class ChillerReadingRow
{
    /// <summary>時間戳記（UTC 時間）。</summary>
    public DateTime Ts { get; init; }
    /// <summary>冰水出水溫度（供水溫度，°C）。</summary>
    public decimal? ChilledWaterOut { get; init; }
    /// <summary>冰水進水溫度（回水溫度，°C）。</summary>
    public decimal? ChilledWaterIn { get; init; }
    /// <summary>冰水溫差（進水溫 - 出水溫，°C）。</summary>
    public decimal? ChilledWaterDelta { get; init; }
    /// <summary>即時/平均輸入功率（kW）。</summary>
    public decimal? InputPowerKw { get; init; }
    /// <summary>累積耗電量或區間用電量（kWh）。</summary>
    public decimal? AccumulatedKwh { get; init; }
    /// <summary>主機負載百分比（0 ~ 100%）。</summary>
    public int? LoadPercentage { get; init; }
    /// <summary>資料讀取狀態數值 (ReadStatus)。</summary>
    public int ReadStatus { get; init; }
    /// <summary>主機累積運轉時數（小時）；於小時聚合表 (1h) 中代表該小時最後一筆讀值。</summary>
    public int? RunningHours { get; init; }
}

/// <summary>
/// 冰水主機資料庫倉儲 (<see cref="ChillerRepository"/>)。
/// 
/// 負責冰水主機主檔清單查詢、前台顯示名稱維護、時序歷史讀值查詢（支援原始 raw 與 1h 小時彙總），
/// 以及執行背景小時彙總排程的批次計算與寫入 (rollup_chiller_1h)。
/// </summary>
public sealed class ChillerRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 查詢所有處於啟用狀態 (is_active = 1) 的冰水主機設備主檔清單。
    /// 依據 ModbusId 由小到大排序。
    /// </summary>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>冰水主機實體物件清單。</returns>
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
    /// 更新冰水主機在前台呈現的顯示名稱 (DisplayName)。
    /// 僅允許修改別名，不動實體對應之 code 與 modbus_id。
    /// </summary>
    /// <param name="deviceId">冰水主機主鍵 ID。</param>
    /// <param name="displayName">新顯示名稱；傳入 null 代表清空。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步更新作業的 <see cref="Task"/>。</returns>
    public async Task UpdateDisplayNameAsync(int deviceId, string? displayName, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE device_chiller SET display_name = @displayName WHERE id = @deviceId",
            new { deviceId, displayName });
    }

    /// <summary>
    /// 查詢特定冰水主機在指定時間區間內的時序歷史數據。
    /// 支援原始節流落地數據 ("raw") 與一小時彙總數據 ("1h")。
    /// </summary>
    /// <param name="deviceId">目標主機主鍵 ID。</param>
    /// <param name="fromUtc">起算時間（UTC）。</param>
    /// <param name="toUtc">截止時間（UTC）。</param>
    /// <param name="interval">資料間隔粒度："raw" 為原始資料表，"1h" 為小時聚合表。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>冰水主機讀值 Row 集合。</returns>
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
    /// 執行小時時序數據彙總 (Rollup)：將 [fromUtc, toUtcExclusive) 區間內的有效原始讀值，
    /// 依整點分桶並計算平均值、最大最小值、用電差額與運轉分鐘數，寫入 rollup_chiller_1h 資料表。
    /// </summary>
    /// <param name="fromUtc">彙總區間起始時間（UTC，包含）。</param>
    /// <param name="toUtcExclusive">彙總區間結束時間（UTC，不包含）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task UpsertHourlyRollupAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            """
            INSERT INTO rollup_chiller_1h
                (device_id, bucket, avg_load_pct, min_chilled_out, max_chilled_out,
                 min_chilled_in, max_chilled_in, avg_chilled_delta, avg_power_kw, kwh_delta,
                 running_hours, run_minutes,
                 avg_chilled_out, avg_chilled_in, avg_cooling_out, avg_cooling_in,
                 avg_current, avg_voltage, avg_high_pressure, avg_low_pressure,
                 avg_rpm, avg_approach, start_count, alarm_bits)
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
                COUNT(DISTINCT CASE WHEN load_percentage > 0 THEN DATE_FORMAT(ts, '%Y-%m-%d %H:%i') END),
                AVG(chilled_water_out), AVG(chilled_water_in),
                AVG(cooling_water_out), AVG(cooling_water_in),
                AVG(input_current), AVG(input_voltage),
                AVG(high_pressure), AVG(low_pressure),
                AVG(actual_rpm), AVG(approach_temp),
                MAX(start_count),
                BIT_OR(alarm_bits)
            FROM chiller_reading
            WHERE read_status = 1 AND ts >= @fromUtc AND ts < @toUtcExclusive
            GROUP BY device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00')
            ON DUPLICATE KEY UPDATE
                avg_load_pct = VALUES(avg_load_pct),
                min_chilled_out = VALUES(min_chilled_out), max_chilled_out = VALUES(max_chilled_out),
                min_chilled_in = VALUES(min_chilled_in), max_chilled_in = VALUES(max_chilled_in),
                avg_chilled_delta = VALUES(avg_chilled_delta),
                avg_power_kw = VALUES(avg_power_kw), kwh_delta = VALUES(kwh_delta),
                running_hours = VALUES(running_hours), run_minutes = VALUES(run_minutes),
                avg_chilled_out = VALUES(avg_chilled_out), avg_chilled_in = VALUES(avg_chilled_in),
                avg_cooling_out = VALUES(avg_cooling_out), avg_cooling_in = VALUES(avg_cooling_in),
                avg_current = VALUES(avg_current), avg_voltage = VALUES(avg_voltage),
                avg_high_pressure = VALUES(avg_high_pressure), avg_low_pressure = VALUES(avg_low_pressure),
                avg_rpm = VALUES(avg_rpm), avg_approach = VALUES(avg_approach),
                start_count = VALUES(start_count), alarm_bits = VALUES(alarm_bits)
            """,
            new { fromUtc, toUtcExclusive }, commandTimeout: 60);
    }

    /// <summary>Dapper 內部查詢專用之冰水主機資料 Row 物件。</summary>
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
