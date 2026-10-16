namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 系統使用者帳號主檔實體 (<see cref="AppUser"/>)。
/// 對應資料庫 app_user 資料表。
/// 
/// 【統一帳號體系】
/// 本表統一承載平台管理員帳號與各場館之營運人員帳號。
/// 一位使用者可同時擁有平台系統角色 (SystemRoleId) 及加入多個場館 (MerchantMembership)。
/// 所有權限判定皆基於角色展開，不進行單一帳號的例外權限覆寫。
/// </summary>
public sealed class AppUser
{
    /// <summary>使用者主鍵識別碼 (Primary Key)。</summary>
    public int Id { get; set; }

    /// <summary>登入帳號名稱（不分大小寫唯一）。</summary>
    public required string Username { get; set; }

    /// <summary>使用者中文顯示名稱（例如 "張工程師"）。</summary>
    public required string DisplayName { get; set; }

    /// <summary>使用者電子郵件信箱；未填寫時為 null。</summary>
    public string? Email { get; set; }

    /// <summary>密碼之安全雜湊演算法雜湊值（PBKDF2 或 Argon2）。</summary>
    public required string PasswordHash { get; set; }

    /// <summary>帳號是否啟用（false 代表已遭停用，無法登入）。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 被賦予的平台系統層級角色 ID（作用範圍為 Platform）；
    /// 若非 null，代表此帳號具有平台管理權限，可切換至 platform scope 進行跨場館維運。
    /// </summary>
    public int? SystemRoleId { get; set; }

    /// <summary>
    /// 平台最高超級管理員旗標。
    /// 若為 true，具備不受任何限制的跨場館完全控制權限。
    /// </summary>
    public bool IsPlatformAdmin { get; set; }

    /// <summary>
    /// 授權版本號 (AuthVersion)。
    /// 每次使用者密碼變更、角色異動、權限修改或場館開關變動時遞增，
    /// API 驗證 JWT 時會比對版本號，版本不符即刻拒絕請求以實現跨線程即時踢除失效 Token。
    /// </summary>
    public int AuthVersion { get; set; } = 1;

    /// <summary>連續登入失敗次數計數器，達到上限時鎖定帳號。</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>帳號鎖定截止之標準 UTC 時間；若未被鎖定則為 null。</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>最後一次成功登入系統之標準 UTC 時間戳記。</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>帳號建立之標準 UTC 時間戳記。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>使用者在各場館的成員資格關聯導覽集合。</summary>
    public List<MerchantMembership> Memberships { get; set; } = new();
}
