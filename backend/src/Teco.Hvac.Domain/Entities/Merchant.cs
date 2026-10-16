namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 場館 / 租戶商家主檔實體 (<see cref="Merchant"/>)。
/// 對應資料庫 merchant 資料表。
/// 
/// 【多租戶架構設計】
/// 系統採用單一資料庫透過 MerchantId 進行邏輯邊界隔離，
/// 提供集團管理多個不同場館或不同營運案場之權限與數據區隔，無需複雜的跨資料庫初始化流程。
/// </summary>
public sealed class Merchant
{
    /// <summary>場館主鍵識別碼 (Primary Key)。</summary>
    public int Id { get; set; }

    /// <summary>場館唯一英數代碼（例如 "teco-golf"、"hq-office"）。</summary>
    public required string Code { get; set; }

    /// <summary>場館繁體中文顯示名稱（例如 "東元高爾夫球俱樂部"）。</summary>
    public required string Name { get; set; }

    /// <summary>場館營運狀態（"active" 啟用中、"suspended" 已停權暫停）。</summary>
    public string Status { get; set; } = "active";

    /// <summary>
    /// 場館是否啟用細部 CRUD 角色權限設定模式。
    /// 若為 false（簡化模式），角色只要勾選某資源即自動獲取該資源完整的建立、讀取、修改、刪除權限。
    /// 僅平台超級管理員具備變更此開關之權限。
    /// </summary>
    public bool IsRoleCrudConfigurationEnabled { get; set; } = true;

    /// <summary>
    /// 場館是否啟用附加子功能細部角色權限設定模式。
    /// 若為 false（簡化模式），角色只要被授予某資源即自動獲取該資源所有的附加子功能按鈕權限。
    /// </summary>
    public bool IsRoleOptionConfigurationEnabled { get; set; } = true;

    /// <summary>場館建立之標準 UTC 時間戳記。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>場館資訊最後異動之標準 UTC 時間戳記。</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
