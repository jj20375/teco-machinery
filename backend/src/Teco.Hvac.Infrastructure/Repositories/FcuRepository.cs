using Dapper;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// FCU 歷史讀值資料列模型。
/// class＋init 屬性，不用 positional record——理由見 MembershipRepository.MerchantUserRow 上的註解。
/// </summary>
public sealed class FcuReadingRow
{
    /// <summary>取樣或統計時間戳記 (UTC)。</summary>
    public DateTime Ts { get; init; }
    /// <summary>開關機狀態（0=關機, 1=開機，rollup 模式可能為 null）。</summary>
    public int? SwitchStatus { get; init; }
    /// <summary>運轉模式（0=冷氣, 1=除濕, 2=送風, 3=暖氣）。</summary>
    public int? Mode { get; init; }
    /// <summary>風速設定（0=弱, 1=中, 2=強, 3=微）。</summary>
    public int? FanSpeed { get; init; }
    /// <summary>感測環境溫度 (°C)。</summary>
    public decimal? Temperature { get; init; }
    /// <summary>通訊讀取狀態（1=成功, 2=逾時, 3=CRC 錯誤, 4=異常碼, 5=未知）。</summary>
    public int ReadStatus { get; init; }
    /// <summary>只有 interval="1h"（查 rollup_fcu_1h）時才有值；raw 模式固定是 null。</summary>
    public int? OnMinutes { get; init; }
}

/// <summary>
/// 給前台每小時趨勢圖用的原始列——只挑聚合需要的欄位，JOIN device_fcu 拿 floor。
/// </summary>
public sealed class FcuHourlyReadingRow
{
    /// <summary>FCU 設備流水號識別碼。</summary>
    public int DeviceId { get; init; }
    /// <summary>讀值時間戳記 (UTC)。</summary>
    public DateTime Ts { get; init; }
    /// <summary>開關機狀態（0=關機, 1=開機）。</summary>
    public int? SwitchStatus { get; init; }
    /// <summary>環境溫度 (°C)。</summary>
    public decimal? Temperature { get; init; }
    /// <summary>通訊狀態碼。</summary>
    public int ReadStatus { get; init; }
    /// <summary>所屬樓層代碼（例如 "1F", "2F"）。</summary>
    public required string Floor { get; init; }
}

/// <summary>
/// 小型冷度送風機 (FCU) 資料存取倉儲。
/// 提供主檔查詢、自訂名稱維護、歷史時序檢索與小時級 Rollup 彙總維護。
/// </summary>
/// <param name="factory">資料庫連線工廠，用於管理 MariaDB 連線生命週期。</param>
public sealed class FcuRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 依樓層條件查詢所有啟用中的 FCU 設備列表。
    /// </summary>
    /// <param name="floor">可選的樓層代碼篩選；若為 null 或空字串則查詢全館設備。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>符合條件的 FCU 設備清單。</returns>
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

    /// <summary>
    /// 依設備流水號查詢單一 FCU 設備主檔。
    /// </summary>
    /// <param name="deviceId">FCU 設備識別碼。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>FCU 實體物件；若查無資料則回傳 null。</returns>
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
    /// <param name="deviceId">目標 FCU 設備識別碼。</param>
    /// <param name="displayName">欲更新的自訂名稱；若為 null 則清除。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    public async Task UpdateDisplayNameAsync(int deviceId, string? displayName, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE device_fcu SET display_name = @displayName WHERE id = @deviceId",
            new { deviceId, displayName });
    }

    /// <summary>
    /// interval="raw" 直接查 fcu_reading；interval="1h" 查 rollup_fcu_1h（比照
    /// ChillerRepository.GetHistoryAsync 的寫法）。1h 模式的 mode/fan_speed 是該小時出現最多次的值
    /// （見 UpsertHourlyRollupAsync）；013 migration 之前的舊彙總列這兩欄是 null。
    /// </summary>
    /// <param name="deviceId">目標 FCU 設備識別碼。</param>
    /// <param name="fromUtc">起算時間 (UTC)。</param>
    /// <param name="toUtc">截止時間 (UTC)。</param>
    /// <param name="interval">檢索精度："raw"（原始採樣）或 "1h"（小時級彙總）。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>時序讀值列表。</returns>
    public async Task<IReadOnlyList<FcuReadingRow>> GetHistoryAsync(
        int deviceId, DateTime fromUtc, DateTime toUtc, string interval = "raw", CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);

        if (interval == "1h")
        {
            var rollupRows = await conn.QueryAsync<FcuReadingRow>(
                "SELECT bucket AS Ts, NULL AS SwitchStatus, mode AS Mode, fan_speed AS FanSpeed, " +
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
    /// 模式／風速取該小時出現最多次的值（同票取較晚出現的），排除 Unknown(-1)——
    /// 平均值對列舉沒有意義，「最後一筆」又會被整點前剛好切換的那一下帶偏。
    /// </summary>
    /// <param name="fromUtc">聚合起點時間 (UTC，含)。</param>
    /// <param name="toUtcExclusive">聚合終點時間 (UTC，不含)。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    public async Task UpsertHourlyRollupAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            """
            INSERT INTO rollup_fcu_1h (device_id, bucket, avg_temp, min_temp, max_temp, on_minutes, mode, fan_speed)
            SELECT a.device_id, a.bucket, a.avg_temp, a.min_temp, a.max_temp, a.on_minutes, m.val, f.val
            FROM (
                SELECT
                    device_id,
                    DATE_FORMAT(ts, '%Y-%m-%d %H:00:00') AS bucket,
                    AVG(temperature) AS avg_temp, MIN(temperature) AS min_temp, MAX(temperature) AS max_temp,
                    COUNT(DISTINCT CASE WHEN switch_status = 1 THEN DATE_FORMAT(ts, '%Y-%m-%d %H:%i') END) AS on_minutes
                FROM fcu_reading
                WHERE read_status = 1 AND ts >= @fromUtc AND ts < @toUtcExclusive
                GROUP BY device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00')
            ) a
            LEFT JOIN (
                SELECT device_id, bucket, val FROM (
                    SELECT device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00') AS bucket, mode AS val,
                           ROW_NUMBER() OVER (PARTITION BY device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00')
                                              ORDER BY COUNT(*) DESC, MAX(ts) DESC) AS rn
                    FROM fcu_reading
                    WHERE read_status = 1 AND mode <> -1 AND ts >= @fromUtc AND ts < @toUtcExclusive
                    GROUP BY device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00'), mode
                ) ranked WHERE rn = 1
            ) m ON m.device_id = a.device_id AND m.bucket = a.bucket
            LEFT JOIN (
                SELECT device_id, bucket, val FROM (
                    SELECT device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00') AS bucket, fan_speed AS val,
                           ROW_NUMBER() OVER (PARTITION BY device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00')
                                              ORDER BY COUNT(*) DESC, MAX(ts) DESC) AS rn
                    FROM fcu_reading
                    WHERE read_status = 1 AND fan_speed <> -1 AND ts >= @fromUtc AND ts < @toUtcExclusive
                    GROUP BY device_id, DATE_FORMAT(ts, '%Y-%m-%d %H:00:00'), fan_speed
                ) ranked WHERE rn = 1
            ) f ON f.device_id = a.device_id AND f.bucket = a.bucket
            ON DUPLICATE KEY UPDATE
                avg_temp = VALUES(avg_temp), min_temp = VALUES(min_temp), max_temp = VALUES(max_temp),
                on_minutes = VALUES(on_minutes), mode = VALUES(mode), fan_speed = VALUES(fan_speed)
            """,
            new { fromUtc, toUtcExclusive }, commandTimeout: 60);
    }

    /// <summary>
    /// 給每小時趨勢圖用：撈出區間內全部 FCU 的讀值（含 floor），小時分桶跟時區換算留給
    /// 呼叫端在 C# 做——MariaDB 這個容器的 time_zone 是 SYSTEM／沒載入具名時區表，
    /// CONVERT_TZ 不可靠；一天全部 FCU 大約 13~14 萬筆，交給 C# 聚合效能上完全沒問題。
    /// </summary>
    /// <param name="fromUtc">起算時間 (UTC)。</param>
    /// <param name="toUtc">截止時間 (UTC)。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>包含樓層資訊的原始統計讀值清單。</returns>
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

    /// <summary>
    /// Dapper 映射使用的 FCU 主檔內部資料列結構。
    /// </summary>
    private sealed class FcuRow
    {
        /// <summary>FCU 設備識別碼 PK。</summary>
        public int Id { get; init; }
        /// <summary>所屬 RS-485 實體通訊通道 (1~4)。</summary>
        public int Channel { get; init; }
        /// <summary>集中控制器 Modbus Station ID (1~4)。</summary>
        public int StationId { get; init; }
        /// <summary>控制器內索引位置 (1~64)。</summary>
        public int Position { get; init; }
        /// <summary>Modbus 保持暫存器起始位址。</summary>
        public int Address { get; init; }
        /// <summary>安裝樓層（例如 "1F"）。</summary>
        public required string Floor { get; init; }
        /// <summary>所屬分區代碼（例如 "A"）。</summary>
        public string? ZoneCode { get; init; }
        /// <summary>場館自訂顯示名稱。</summary>
        public string? DisplayName { get; init; }
        /// <summary>是否啟用監控。</summary>
        public bool IsActive { get; init; }
    }
}
