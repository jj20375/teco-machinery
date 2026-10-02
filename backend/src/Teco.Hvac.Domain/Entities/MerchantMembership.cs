namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 使用者在單一場館的角色指派（拿掉 TenantId／closure table——
/// TECO 的場館是扁平的，沒有多層組織樹要繼承，一個 membership 就是一個場館+一個角色）。
/// </summary>
public sealed class MerchantMembership
{
    public int Id { get; set; }
    public required int MerchantId { get; set; }
    public required int UserId { get; set; }
    public int? RoleId { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>場館擁有者（第一位管理員）：不能被刪除、停用、改角色；其他管理員的管理動作只有擁有者能做。</summary>
    public bool IsOwner { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
