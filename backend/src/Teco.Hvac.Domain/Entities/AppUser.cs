namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 所有登入主體統一用這張表（比照美達特：PlatformUser 一張表同時涵蓋平台帳號與商家/場館帳號）。
/// 一個人可以同時是平台帳號（SystemRoleId 非空）與多個場館的成員（見 MerchantMembership），
/// 但 v1 不支援個別帳號的權限覆寫——權限一律從角色算出。
/// </summary>
public sealed class AppUser
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string DisplayName { get; set; }
    public string? Email { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>指派的平台系統角色（Scope=Platform）。非空代表這是平台系統帳號，可切到 platform scope 操作。</summary>
    public int? SystemRoleId { get; set; }

    /// <summary>平台超級管理員旗標（跨場館最高權限，即使沒有另外指派 SystemRoleId 也視為平台管理員）。</summary>
    public bool IsPlatformAdmin { get; set; }

    /// <summary>授權版本號。密碼、角色、權限、場館 CRUD/子項開關變更時遞增，使舊 JWT 立即失效。</summary>
    public int AuthVersion { get; set; } = 1;

    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<MerchantMembership> Memberships { get; set; } = new();
}
