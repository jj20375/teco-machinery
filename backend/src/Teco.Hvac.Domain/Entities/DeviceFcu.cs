using Teco.Hvac.Contracts;

namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// FCU 主檔。唯一鍵是 (Channel, StationId, Position)，不是 ID 字串
/// （說明書 6.1：FC_MC1_01 這類 ID 會跨 DDC 重複）。
/// ZoneCode 對應前台圖面分區（例如 B1-Z07），由後台維護，不寫死在前端。
/// </summary>
public sealed class DeviceFcu
{
    public int Id { get; set; }
    public required Channel Channel { get; set; }
    public required byte StationId { get; set; }
    public required int Position { get; set; }
    public required ushort Address { get; set; }
    public required string Floor { get; set; }
    public string? ZoneCode { get; set; }
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
}
