using Dapper;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 設備主檔查詢。Collector 啟動時用這裡的對照表把 (Channel, StationId, Position)
/// 轉成資料庫的 device_id；找不到對照的 FCU 視為尚未登記，記警告但不中斷寫入。
/// </summary>
/// <param name="factory">資料庫連線工廠，用於建立與管理 MariaDB 連線生命週期。</param>
public sealed class DeviceRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 依 Modbus ID 索引載入所有啟用中的冰水主機主檔。
    /// </summary>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>以 ModbusId (1~4) 為鍵、<see cref="DeviceChiller"/> 實體為值的唯讀字典。</returns>
    public async Task<IReadOnlyDictionary<int, DeviceChiller>> GetChillersByModbusIdAsync(CancellationToken ct = default)
    {
        // 建立並開啟資料庫連線
        using var conn = await factory.CreateOpenAsync(ct);
        // 查詢所有 is_active = 1 的冰水主機資料列
        var rows = await conn.QueryAsync<DeviceChillerRow>(
            "SELECT id, code, modbus_id AS ModbusId, display_name AS DisplayName, " +
            "rated_capacity_rt AS RatedCapacityRt, is_active AS IsActive FROM device_chiller WHERE is_active = 1");

        // 轉換為以 ModbusId 為鍵的實體字典
        return rows.ToDictionary(
            r => r.ModbusId,
            r => new DeviceChiller
            {
                Id = r.Id,
                Code = r.Code,
                ModbusId = r.ModbusId,
                DisplayName = r.DisplayName,
                RatedCapacityRt = r.RatedCapacityRt,
                IsActive = r.IsActive,
            });
    }

    /// <summary>
    /// 依實體定位座標載入所有啟用中的送風機 (FCU) 對照表。
    /// 鍵為 (Channel, StationId, Position)，對應說明書 6.1 的唯一定位方式。Floor 供告警規則按樓層套用。
    /// </summary>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>以 (Channel, StationId, Position) 為鍵、(Id, Floor) 元組為值的唯讀字典。</returns>
    public async Task<IReadOnlyDictionary<(Channel Channel, byte StationId, int Position), (int Id, string Floor)>> GetFcuIdMapAsync(
        CancellationToken ct = default)
    {
        // 建立並開啟資料庫連線
        using var conn = await factory.CreateOpenAsync(ct);
        // 查詢所有啟用中 FCU 的識別碼與實體座標對應
        var rows = await conn.QueryAsync<(int Id, int Channel, byte StationId, int Position, string Floor)>(
            "SELECT id AS Id, channel AS Channel, station_id AS StationId, position AS Position, floor AS Floor " +
            "FROM device_fcu WHERE is_active = 1");

        // 組裝成以實體位置三元組為鍵的映射字典
        return rows.ToDictionary(
            r => ((Channel)r.Channel, r.StationId, r.Position),
            r => (r.Id, r.Floor));
    }

    /// <summary>
    /// Dapper 專用的冰水主機資料庫映射資料列模型。
    /// </summary>
    private sealed class DeviceChillerRow
    {
        /// <summary>主機流水號 PK。</summary>
        public int Id { get; init; }
        /// <summary>主機內部編碼（例如 "CH-01"）。</summary>
        public required string Code { get; init; }
        /// <summary>通訊 Modbus 站號 (1~4)。</summary>
        public int ModbusId { get; init; }
        /// <summary>前台顯示名稱。</summary>
        public required string DisplayName { get; init; }
        /// <summary>額定冷凍噸位 (RT)。</summary>
        public decimal? RatedCapacityRt { get; init; }
        /// <summary>是否啟用監控。</summary>
        public bool IsActive { get; init; }
    }
}
