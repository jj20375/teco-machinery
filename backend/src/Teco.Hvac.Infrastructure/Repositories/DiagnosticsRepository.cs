using Dapper;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 平台「系統診斷」專用時序資料庫診斷倉儲 (<see cref="DiagnosticsRepository"/>)。
/// 
/// 【效能極致最佳化】
/// 時序資料表 chiller_reading 與 fcu_reading 之複合主鍵為 (device_id, ts)，
/// 若直接對整張大表執行 MAX(ts) 會觸發百萬級別全分區掃描，導致 5 秒輪詢時拖垮資料庫。
/// 本倉儲透過 STRAIGHT_JOIN 由小型設備主檔 (device_chiller / device_fcu) 驅動，
/// 配合精準主鍵索引在數毫秒內完成寫入健康度、活躍設備數與小時聚合狀態之快速聚合診斷。
/// </summary>
public sealed class DiagnosticsRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 時序落地與彙總健康狀態統計 Row 物件 (<see cref="PersistenceStatsRow"/>)。
    /// </summary>
    public sealed class PersistenceStatsRow
    {
        /// <summary>冰水主機最後成功寫入時序表的時間戳記 (ts)。</summary>
        public DateTime? ChillerLatestTs { get; init; }
        /// <summary>冰水主機最後成功接收到的時間戳記 (received_at)。</summary>
        public DateTime? ChillerLatestReceivedAt { get; init; }
        /// <summary>冰水主機在指定近期間隔內成功寫入的總筆數。</summary>
        public long ChillerRecentRows { get; init; }
        /// <summary>近期有成功回報數據之活躍冰機台數。</summary>
        public long ChillerRecentDevices { get; init; }
        /// <summary>系統中目前啟用的冰水主機總台數。</summary>
        public long ChillerActiveDevices { get; init; }

        /// <summary>FCU 最後成功寫入時序表的時間戳記 (ts)。</summary>
        public DateTime? FcuLatestTs { get; init; }
        /// <summary>FCU 在指定近期間隔內成功寫入的總筆數。</summary>
        public long FcuRecentRows { get; init; }
        /// <summary>近期有成功回報數據之活躍 FCU 台數。</summary>
        public long FcuRecentDevices { get; init; }
        /// <summary>系統中目前啟用的 FCU 總台數。</summary>
        public long FcuActiveDevices { get; init; }

        /// <summary>冰水主機 1 小時聚合資料表 (rollup_chiller_1h) 最新完成之 Bucket 時間點。</summary>
        public DateTime? RollupChillerLatestBucket { get; init; }
        /// <summary>FCU 1 小時聚合資料表 (rollup_fcu_1h) 最新完成之 Bucket 時間點。</summary>
        public DateTime? RollupFcuLatestBucket { get; init; }
    }

    /// <summary>
    /// 查詢時序表最近的資料落地統計與小時聚合進度指標。
    /// </summary>
    /// <param name="recentSinceUtc">近期活躍統計之起算標準 UTC 時間點（例如最近 15 分鐘）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>包含時序落地與聚合狀態之統計 Row 物件。</returns>
    public async Task<PersistenceStatsRow> GetPersistenceStatsAsync(DateTime recentSinceUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.QuerySingleAsync<PersistenceStatsRow>(
            """
            SELECT
              (SELECT MAX((SELECT MAX(r.ts) FROM chiller_reading r WHERE r.device_id = d.id AND r.read_status = 1)) FROM device_chiller d)  AS ChillerLatestTs,
              (SELECT MAX(r.received_at) FROM device_chiller d STRAIGHT_JOIN chiller_reading r ON r.device_id = d.id AND r.ts >= @since AND r.read_status = 1) AS ChillerLatestReceivedAt,
              (SELECT COUNT(*) FROM device_chiller d STRAIGHT_JOIN chiller_reading r ON r.device_id = d.id AND r.ts >= @since AND r.read_status = 1) AS ChillerRecentRows,
              (SELECT COUNT(DISTINCT r.device_id) FROM device_chiller d STRAIGHT_JOIN chiller_reading r ON r.device_id = d.id AND r.ts >= @since AND r.read_status = 1) AS ChillerRecentDevices,
              (SELECT COUNT(*) FROM device_chiller WHERE is_active = 1)                                              AS ChillerActiveDevices,
              (SELECT MAX((SELECT MAX(r.ts) FROM fcu_reading r WHERE r.device_id = d.id AND r.read_status = 1)) FROM device_fcu d)          AS FcuLatestTs,
              (SELECT COUNT(*) FROM device_fcu d STRAIGHT_JOIN fcu_reading r ON r.device_id = d.id AND r.ts >= @since AND r.read_status = 1) AS FcuRecentRows,
              (SELECT COUNT(DISTINCT r.device_id) FROM device_fcu d STRAIGHT_JOIN fcu_reading r ON r.device_id = d.id AND r.ts >= @since AND r.read_status = 1) AS FcuRecentDevices,
              (SELECT COUNT(*) FROM device_fcu WHERE is_active = 1)                                                  AS FcuActiveDevices,
              (SELECT MAX((SELECT MAX(r.bucket) FROM rollup_chiller_1h r WHERE r.device_id = d.id)) FROM device_chiller d) AS RollupChillerLatestBucket,
              (SELECT MAX((SELECT MAX(r.bucket) FROM rollup_fcu_1h r WHERE r.device_id = d.id)) FROM device_fcu d)    AS RollupFcuLatestBucket
            """,
            new { since = recentSinceUtc });
    }
}
