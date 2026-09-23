namespace Teco.Hvac.Domain.Entities;

/// <summary>角色作用範圍。Platform＝跨場館的平台管理；Merchant＝單一場館範圍。</summary>
public enum RoleScope { Platform, Merchant }

/// <summary>
/// 平台或場館範圍的角色定義（比照美達特 PlatformRole）。
/// </summary>
public sealed class AppRole
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required RoleScope Scope { get; set; }

    /// <summary>特定場館自訂角色的場館 ID；系統內建的通用角色（platform-admin/merchant-admin/editor/viewer）為 null。</summary>
    public int? MerchantId { get; set; }

    /// <summary>是否為內建系統角色（不可從介面刪除；Code/Name 鎖定）。</summary>
    public bool IsSystem { get; set; }

    /// <summary>true 時永遠自動同步為目前系統所有同 Scope 權限的完整 CRUD＋全部子功能（platform-admin／merchant-admin 用）。</summary>
    public bool IsFullAccess { get; set; }

    /// <summary>true 代表平台管理員已手動調整過這個系統角色的權限；重新執行 Seeder 時不再覆寫其 RolePermission。</summary>
    public bool IsPermissionsCustomized { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
