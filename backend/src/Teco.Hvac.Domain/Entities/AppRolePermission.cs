namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 角色與權限之 CRUD 動作及附加子功能關聯實體 (<see cref="AppRolePermission"/>)。
/// 對應資料庫 app_role_permission 資料表。
/// 
/// 資料庫內部一律儲存管理員實際勾選的細部真相；
/// 場館階層的簡化設定開關僅於動態簽發 JWT 權杖時影響展開策略，不會變動本關聯表。
/// </summary>
public sealed class AppRolePermission
{
    /// <summary>所屬角色 ID (RoleId)。</summary>
    public required int RoleId { get; set; }

    /// <summary>所屬權限資源 ID (PermissionId)。</summary>
    public required int PermissionId { get; set; }

    /// <summary>是否具備「新增 / 建立 (Create)」動作權限。</summary>
    public bool PerCreate { get; set; }

    /// <summary>是否具備「查詢 / 讀取 (Read)」動作權限。</summary>
    public bool PerRead { get; set; }

    /// <summary>是否具備「編輯 / 更新 (Update)」動作權限。</summary>
    public bool PerUpdate { get; set; }

    /// <summary>是否具備「刪除 / 註銷 (Delete)」動作權限。</summary>
    public bool PerDelete { get; set; }

    /// <summary>
    /// 該角色於此功能下獲授權之附加子功能 Key 清單 JSON 字串（例如 ["ack", "export"]）。
    /// </summary>
    public string OptionsJson { get; set; } = "[]";
}
