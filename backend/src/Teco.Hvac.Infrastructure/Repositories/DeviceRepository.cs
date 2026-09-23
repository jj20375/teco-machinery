using Dapper;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 設備主檔查詢。Collector 啟動時用這裡的對照表把 (Channel, StationId, Position)
/// 轉成資料庫的 device_id；找不到對照的 FCU 視為尚未登記，記警告但不中斷寫入。
/// </summary>
public sealed class DeviceRepository(TecoDbConnectionFactory factory)
{
    public async Task<IReadOnlyDictionary<int, DeviceChiller>> GetChillersByModbusIdAsync(CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<DeviceChillerRow>(
            "SELECT id, code, modbus_id AS ModbusId, display_name AS DisplayName, " +
            "rated_capacity_rt AS RatedCapacityRt, is_active AS IsActive FROM device_chiller WHERE is_active = 1");

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

    /// <summary>鍵為 (Channel, StationId, Position)，對應說明書 6.1 的唯一定位方式。Floor 供告警規則按樓層套用。</summary>
    public async Task<IReadOnlyDictionary<(Channel Channel, byte StationId, int Position), (int Id, string Floor)>> GetFcuIdMapAsync(
        CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<(int Id, int Channel, byte StationId, int Position, string Floor)>(
            "SELECT id AS Id, channel AS Channel, station_id AS StationId, position AS Position, floor AS Floor " +
            "FROM device_fcu WHERE is_active = 1");

        return rows.ToDictionary(
            r => ((Channel)r.Channel, r.StationId, r.Position),
            r => (r.Id, r.Floor));
    }

    private sealed class DeviceChillerRow
    {
        public int Id { get; init; }
        public required string Code { get; init; }
        public int ModbusId { get; init; }
        public required string DisplayName { get; init; }
        public decimal? RatedCapacityRt { get; init; }
        public bool IsActive { get; init; }
    }
}
