using Teco.Hvac.Contracts;

namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// FCU 送風機設備主檔實體 (<see cref="DeviceFcu"/>)。
/// 對應資料庫 device_fcu 資料表。
/// 
/// 【定址識別規則】
/// 實體設備之唯一識別複合鍵為 (Channel, StationId, Position)，而非 ID 字串
/// （依原廠說明書 §6.1 提示，FC_MC1_01 這類代碼可能跨不同 DDC 重複出現）。
/// ZoneCode 則對應前台平面圖之溫控分區代碼（例如 "B1-Z07"），由後台管理員維護，非程式寫死。
/// </summary>
public sealed class DeviceFcu
{
    /// <summary>FCU 設備在資料庫中的主鍵識別碼 (Primary Key)。</summary>
    public int Id { get; set; }

    /// <summary>所屬實體通訊通道（Channel.Ddc1 對應 B1 樓層；Channel.Ddc2 對應 B2 樓層）。</summary>
    public required Channel Channel { get; set; }

    /// <summary>所屬 DDC 控制器之站號（Station ID，數值 0 ~ 255）。</summary>
    public required byte StationId { get; set; }

    /// <summary>於該站控制器內部之位置索引序號（Position，自 0 起算）。</summary>
    public required int Position { get; set; }

    /// <summary>硬體 Modbus 或底層定址位址。</summary>
    public required ushort Address { get; set; }

    /// <summary>設備所在樓層代碼（例如 "B1"、"B2"）。</summary>
    public required string Floor { get; set; }

    /// <summary>所屬之平面圖溫控分區代碼（例如 "B1-Z07"）；未設定分區時為 null。</summary>
    public string? ZoneCode { get; set; }

    /// <summary>前端介面顯示名稱或圖面編號（例如 "FC_MC1_01"）。</summary>
    public string? DisplayName { get; set; }

    /// <summary>設備是否處於啟用狀態（true 代表納入時序落地與監控範圍）。</summary>
    public bool IsActive { get; set; } = true;
}
