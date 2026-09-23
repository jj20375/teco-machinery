namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 角色與權限的 CRUD／子功能授予關聯（比照美達特 PlatformRolePermission）。
/// 資料庫永遠存細項真相；場館的 CRUD/子項簡化開關只影響「發 JWT 時怎麼展開」，不改這張表。
/// </summary>
public sealed class AppRolePermission
{
    public required int RoleId { get; set; }
    public required int PermissionId { get; set; }

    public bool PerCreate { get; set; }
    public bool PerRead { get; set; }
    public bool PerUpdate { get; set; }
    public bool PerDelete { get; set; }

    /// <summary>勾選之附加子功能 Key 陣列 JSON，例如 ["ack"]。</summary>
    public string OptionsJson { get; set; } = "[]";
}
