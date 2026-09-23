namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 使用者在單一場館的角色指派（比照美達特 CustomerMembership，但拿掉 TenantId／closure table——
/// TECO 的場館是扁平的，沒有 Metat 那種診所內部組織樹要繼承，一個 membership 就是一個場館+一個角色）。
/// </summary>
public sealed class MerchantMembership
{
    public int Id { get; set; }
    public required int MerchantId { get; set; }
    public required int UserId { get; set; }
    public int? RoleId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
