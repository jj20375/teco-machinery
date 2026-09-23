namespace Teco.Hvac.Domain.Entities;

/// <summary>冰水主機主檔。現場固定兩台（漢鐘 Modbus ID 1、2），但保留為資料表以便擴充。</summary>
public sealed class DeviceChiller
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required int ModbusId { get; set; }
    public required string DisplayName { get; set; }
    /// <summary>額定容量 RT，未提供時為 null；用於計算 kW/RT 效率，待供應商/東元確認。</summary>
    public decimal? RatedCapacityRt { get; set; }
    public bool IsActive { get; set; } = true;
}
