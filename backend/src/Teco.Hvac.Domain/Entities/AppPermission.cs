namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 系統權限目錄項目實體 (<see cref="AppPermission"/>)。
/// 對應資料庫 app_permission 資料表。
/// 
/// 定義系統受控資源代碼、選單階層結構以及該功能所定義的附加子功能清單。
/// 細部的 CRUD 動作則由關聯表 <see cref="AppRolePermission"/> 之布林旗標表示。
/// </summary>
public sealed class AppPermission
{
    /// <summary>權限項目主鍵識別碼 (Primary Key)。</summary>
    public int Id { get; set; }

    /// <summary>權限資源唯一代碼（例如 "hvac.chiller"、"hvac.fcu"、"system.users"）。</summary>
    public required string Code { get; set; }

    /// <summary>權限項目繁體中文名稱（例如 "冰水主機監控"、"帳號權限管理"）。</summary>
    public required string Name { get; set; }

    /// <summary>權限項目之詳細說明與用途簡述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>權限所屬之作用範圍（Platform 或 Merchant）。</summary>
    public required RoleScope Scope { get; set; }

    /// <summary>樹狀結構中的父層節點 ID，供導航選單或分類歸納使用；不影響授權裁決。</summary>
    public int? ParentId { get; set; }

    /// <summary>於選單介面中的排序權重。</summary>
    public int SortOrder { get; set; }

    /// <summary>是否作為前端導航選單項目呈現於側邊欄中。</summary>
    public bool IsMenu { get; set; }

    /// <summary>該功能對應的前端畫面路由路徑（例如 "/admin/chiller"）。</summary>
    public string? RoutePath { get; set; }

    /// <summary>
    /// 該權限資源支援之附加子功能定義 JSON（例如 [{"key":"ack","name":"確認告警"}]）。
    /// </summary>
    public string SubFeaturesJson { get; set; } = "[]";

    /// <summary>權限項目建立之標準 UTC 時間戳記。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 附加子功能鍵值與顯示名稱記錄物件 (<see cref="SubFeature"/>)。
/// 對應 <see cref="AppPermission.SubFeaturesJson"/> 陣列中的單一元素。
/// </summary>
/// <param name="Key">子功能識別代碼（例如 "ack"、"export"、"override"）。</param>
/// <param name="Name">子功能之繁體中文顯示標籤（例如 "確認告警"、"匯出報表"）。</param>
public sealed record SubFeature(string Key, string Name);
