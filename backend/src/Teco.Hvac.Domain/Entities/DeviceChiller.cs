namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 冰水主機設備主檔實體 (<see cref="DeviceChiller"/>)。
/// 對應資料庫 device_chiller 資料表。現場固定配置兩台主機（Modbus ID 分別為 1 與 2），由資料表管理以保留日後擴充彈性。
/// </summary>
public sealed class DeviceChiller
{
    /// <summary>冰水主機在資料庫中的主鍵識別碼 (Primary Key)。</summary>
    public int Id { get; set; }

    /// <summary>冰水主機系統唯一代碼（例如 "CH-01"、"CH-02"）。</summary>
    public required string Code { get; set; }

    /// <summary>此主機在硬體 RS-485 Modbus 匯流排上之站號（現場為 1 或 2）。</summary>
    public required int ModbusId { get; set; }

    /// <summary>冰水主機於前端介面呈現之顯示名稱（例如 "1號冰水主機"）。</summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// 主機額定冷凍噸容量（Rated Capacity RT）。
    /// 未提供時為 null；用於計算主機運轉效率指標（kW/RT），數值待設備銘牌或供應商確認。
    /// </summary>
    public decimal? RatedCapacityRt { get; set; }

    /// <summary>主機是否處於啟用狀態（true 代表納入監控與採集範圍）。</summary>
    public bool IsActive { get; set; } = true;
}
