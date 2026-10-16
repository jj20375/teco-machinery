namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 角色權限適用之階層範圍列舉 (<see cref="RoleScope"/>)。
/// </summary>
public enum RoleScope
{
    /// <summary>跨場館的平台系統管理層級（例如 platform-admin、platform-operator）。</summary>
    Platform,
    /// <summary>單一場館/商家營運層級（例如 merchant-admin、editor、viewer）。</summary>
    Merchant
}

/// <summary>
/// 系統或場館之角色定義實體 (<see cref="AppRole"/>)。
/// 對應資料庫 app_role 資料表。
/// </summary>
public sealed class AppRole
{
    /// <summary>角色主鍵識別碼 (Primary Key)。</summary>
    public int Id { get; set; }

    /// <summary>角色唯一代碼字串（例如 "platform-admin"、"merchant-admin"、"shift-engineer"）。</summary>
    public required string Code { get; set; }

    /// <summary>角色的繁體中文顯示名稱（例如 "場館管理員"、"值班工程師"）。</summary>
    public required string Name { get; set; }

    /// <summary>角色所屬之作用範圍（Platform 或 Merchant）。</summary>
    public required RoleScope Scope { get; set; }

    /// <summary>
    /// 若為特定場館所建立的自訂角色，記錄其場館 ID；
    /// 若為系統全域內建預設角色，則固定為 null。
    /// </summary>
    public int? MerchantId { get; set; }

    /// <summary>是否為系統內建固定角色（系統角色不可從使用者介面刪除，且代碼與名稱鎖定保護）。</summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// 是否為完全存取角色（Full Access）。
    /// 若為 true，系統在初始化或更新權限目錄時，會自動為此角色賦予該 Scope 下全部資源的完整 CRUD 與所有附加子功能。
    /// </summary>
    public bool IsFullAccess { get; set; }

    /// <summary>
    /// 是否已經過管理員手動微調客製化權限。
    /// 若為 true，資料庫 Seeder 重啟時將保留管理員的設定，不再強制覆寫其權限表。
    /// </summary>
    public bool IsPermissionsCustomized { get; set; }

    /// <summary>角色建立之標準 UTC 時間戳記。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
