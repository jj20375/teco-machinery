namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 使用者於指定場館的成員資格與角色關聯實體 (<see cref="MerchantMembership"/>)。
/// 對應資料庫 merchant_membership 資料表。
/// 
/// 【組織授權架構】
/// 系統之場館架構為扁平模型，單一 Membership 代表「某個使用者在特定場館中被賦予的單一角色」。
/// </summary>
public sealed class MerchantMembership
{
    /// <summary>成員資格主鍵識別碼 (Primary Key)。</summary>
    public int Id { get; set; }

    /// <summary>所屬場館的 MerchantId 外鍵。</summary>
    public required int MerchantId { get; set; }

    /// <summary>對應之使用者 UserId 外鍵。</summary>
    public required int UserId { get; set; }

    /// <summary>該使用者在此場館中所指派的角色 ID (RoleId)；未指派特定角色時為 null。</summary>
    public int? RoleId { get; set; }

    /// <summary>此成員資格在此場館中是否處於有效啟用狀態。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 是否為場館擁有者 (Owner)。
    /// 擁有者為場館第一管理員，不可被刪除、停用或降級；對其他場館管理員的帳號操作僅擁有者有權執行。
    /// </summary>
    public bool IsOwner { get; set; }

    /// <summary>資格建立之標準 UTC 時間戳記。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
