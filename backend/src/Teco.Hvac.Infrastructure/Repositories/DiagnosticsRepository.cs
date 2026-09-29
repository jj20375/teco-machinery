using Dapper;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 平台「系統診斷」頁用的落地統計：資料有沒有真的寫進時序表、聚合排程有沒有把整點補上。
/// 只讀、只做彙總，不回傳任何原始列。
///
/// 時序表的主鍵是 (device_id, ts)，直接對整張表 MAX(ts) 或 WHERE ts >= ... 用不到索引、
/// 會掃過整個月份分割區（FCU 一個月上百萬筆），診斷頁又是 5 秒輪詢一次，所以一律改成
/// 「從設備主檔逐台走主鍵」的寫法（相關子查詢／STRAIGHT_JOIN 強制以設備表驅動）。
///
/// 只算 read_status = 1（Success）的列：2026-09-29 以前 Collector 在通道斷線時仍會照節流週期寫入
/// read_status = Disconnected 的列（現已改成不寫，但舊資料還在），不過濾的話「連不上設備」也會被
/// 誤判成「資料庫寫入正常」。
/// </summary>
public sealed class DiagnosticsRepository(TecoDbConnectionFactory factory)
{
    /// <summary>時間欄位一律 DateTime（UTC），由呼叫端 AsUtcOffset() 轉換——見 CLAUDE.md 原則 1。</summary>
    public sealed class PersistenceStatsRow
    {
        public DateTime? ChillerLatestTs { get; init; }
        public DateTime? ChillerLatestReceivedAt { get; init; }
        public long ChillerRecentRows { get; init; }
        public long ChillerRecentDevices { get; init; }
        public long ChillerActiveDevices { get; init; }
        public DateTime? FcuLatestTs { get; init; }
        public long FcuRecentRows { get; init; }
        public long FcuRecentDevices { get; init; }
        public long FcuActiveDevices { get; init; }
        public DateTime? RollupChillerLatestBucket { get; init; }
        public DateTime? RollupFcuLatestBucket { get; init; }
    }

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
