using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 場館成員列表查詢專用 Row 物件 (<see cref="MerchantUserRow"/>)。
/// 結合了 app_user、merchant_membership 與 app_role 三張資料表，供前台成員列表介面直接渲染。
/// </summary>
public sealed class MerchantUserRow
{
    /// <summary>成員資格主鍵 ID (merchant_membership.id)。</summary>
    public int MembershipId { get; init; }
    /// <summary>使用者主鍵 ID (app_user.id)。</summary>
    public int UserId { get; init; }
    /// <summary>登入帳號名稱。</summary>
    public required string Username { get; init; }
    /// <summary>使用者中文顯示名稱。</summary>
    public required string DisplayName { get; init; }
    /// <summary>電子郵件信箱。</summary>
    public string? Email { get; init; }
    /// <summary>成員資格在此場館中是否啟用。</summary>
    public bool IsActive { get; init; }
    /// <summary>是否為場館擁有者 (Owner)。</summary>
    public bool IsOwner { get; init; }
    /// <summary>指派的角色 ID。</summary>
    public int? RoleId { get; init; }
    /// <summary>角色代碼（例如 "merchant-admin"）。</summary>
    public string? RoleCode { get; init; }
    /// <summary>角色中文名稱（例如 "場館管理員"）。</summary>
    public string? RoleName { get; init; }
    /// <summary>最後登入標準 UTC 時間戳記。</summary>
    public DateTimeOffset? LastLoginAt { get; init; }
    /// <summary>帳號鎖定截止時間 (UTC)。</summary>
    public DateTimeOffset? LockedUntil { get; init; }
}

/// <summary>
/// 場館成員資格資料庫倉儲 (<see cref="MembershipRepository"/>)。
/// 
/// 負責 merchant_membership 資料表之存取操作。
/// 維護使用者與場館之間的歸屬關聯、角色指派、擁有者狀態與成員註銷。
/// </summary>
public sealed class MembershipRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 查詢指定使用者在所有場館中目前處於啟用狀態 (is_active = 1) 的成員資格清單。
    /// </summary>
    /// <param name="userId">使用者主鍵 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>該使用者的有效成員資格實體清單。</returns>
    public async Task<IReadOnlyList<MerchantMembership>> ListActiveForUserAsync(int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<MembershipRow>(
            """
            SELECT id, merchant_id AS MerchantId, user_id AS UserId, role_id AS RoleId, is_active AS IsActive, is_owner AS IsOwner, created_at AS CreatedAt
            FROM merchant_membership WHERE user_id = @userId AND is_active = 1
            ORDER BY created_at
            """, new { userId });
        return rows.Select(ToEntity).ToList();
    }

    /// <summary>
    /// 依據成員資格主鍵 ID 查詢單筆資格紀錄。
    /// </summary>
    /// <param name="membershipId">資格主鍵 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>成員資格實體物件；若不存在則回傳 null。</returns>
    public async Task<MerchantMembership?> FindByIdAsync(int membershipId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MembershipRow>(
            """
            SELECT id, merchant_id AS MerchantId, user_id AS UserId, role_id AS RoleId, is_active AS IsActive, is_owner AS IsOwner, created_at AS CreatedAt
            FROM merchant_membership WHERE id = @membershipId
            """, new { membershipId });
        return row is null ? null : ToEntity(row);
    }

    /// <summary>
    /// 依據場館 ID 與使用者 ID 查詢特定的成員資格紀錄。
    /// </summary>
    /// <param name="merchantId">場館 ID。</param>
    /// <param name="userId">使用者 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>成員資格實體物件；若無關聯則回傳 null。</returns>
    public async Task<MerchantMembership?> FindAsync(int merchantId, int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MembershipRow>(
            """
            SELECT id, merchant_id AS MerchantId, user_id AS UserId, role_id AS RoleId, is_active AS IsActive, is_owner AS IsOwner, created_at AS CreatedAt
            FROM merchant_membership WHERE merchant_id = @merchantId AND user_id = @userId
            """, new { merchantId, userId });
        return row is null ? null : ToEntity(row);
    }

    /// <summary>
    /// 查詢指定場館轄下的所有成員詳細清單（包含個人資料與被賦予之角色名稱）。
    /// </summary>
    /// <param name="merchantId">目標場館 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>場館成員 Row 物件集合。</returns>
    public async Task<IReadOnlyList<MerchantUserRow>> ListForMerchantAsync(
        int merchantId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<MerchantUserRow>(
            """
            SELECT m.id AS MembershipId, m.user_id AS UserId, u.username AS Username, u.display_name AS DisplayName,
                   u.email AS Email, m.is_active AS IsActive, m.is_owner AS IsOwner, m.role_id AS RoleId, r.code AS RoleCode, r.name AS RoleName,
                   u.last_login_at AS LastLoginAt, u.locked_until AS LockedUntil
            FROM merchant_membership m
            JOIN app_user u ON u.id = m.user_id
            LEFT JOIN app_role r ON r.id = m.role_id
            WHERE m.merchant_id = @merchantId
            ORDER BY m.created_at
            """, new { merchantId });
        return rows.ToList();
    }

    /// <summary>
    /// 建立一筆全新的場館成員資格關聯。
    /// </summary>
    /// <param name="membership">欲建立之資格實體物件。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>建立後之資料庫自動遞增主鍵 ID。</returns>
    public async Task<int> CreateAsync(MerchantMembership membership, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO merchant_membership (merchant_id, user_id, role_id, is_active, is_owner)
            VALUES (@MerchantId, @UserId, @RoleId, @IsActive, @IsOwner);
            SELECT LAST_INSERT_ID();
            """, membership);
        return (int)id;
    }

    /// <summary>
    /// 更新成員在此場館中的指派角色或啟用狀態。
    /// 傳入 null 代表保留原有數值不予修改。
    /// </summary>
    /// <param name="membershipId">成員資格主鍵 ID。</param>
    /// <param name="roleId">欲指派之新角色 ID。</param>
    /// <param name="isActive">成員資格是否啟用。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task UpdateRoleAsync(int membershipId, int? roleId, bool? isActive, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            """
            UPDATE merchant_membership
            SET role_id = COALESCE(@roleId, role_id), is_active = COALESCE(@isActive, is_active)
            WHERE id = @membershipId
            """, new { membershipId, roleId, isActive });
    }

    /// <summary>
    /// 刪除指定之場館成員資格（僅解除該使用者與此場館之關聯，不影響其帳號主檔與在其他場館的資格）。
    /// </summary>
    /// <param name="membershipId">資格主鍵 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步刪除作業的 <see cref="Task"/>。</returns>
    public async Task DeleteAsync(int membershipId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM merchant_membership WHERE id = @membershipId", new { membershipId });
    }

    /// <summary>
    /// 檢查指定場館目前是否已經存在至少一位場館擁有者 (Owner)。
    /// </summary>
    /// <param name="merchantId">場館 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>若已存在擁有者回傳 true，否則回傳 false。</returns>
    public async Task<bool> HasOwnerAsync(int merchantId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM merchant_membership WHERE merchant_id = @merchantId AND is_owner = 1", new { merchantId }) > 0;
    }

    /// <summary>
    /// 計算目前指派特定角色之成員資格總人數（刪除自訂角色前進行安全檢查，防止成員瞬間遺失角色權限）。
    /// </summary>
    /// <param name="roleId">角色 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>使用該角色的成員數量。</returns>
    public async Task<int> CountByRoleAsync(int roleId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM merchant_membership WHERE role_id = @roleId", new { roleId });
    }

    /// <summary>內部資料映射轉換方法。</summary>
    private static MerchantMembership ToEntity(MembershipRow r) => new()
    {
        Id = r.Id, MerchantId = r.MerchantId, UserId = r.UserId, RoleId = r.RoleId,
        IsActive = r.IsActive, IsOwner = r.IsOwner, CreatedAt = r.CreatedAt,
    };

    /// <summary>Dapper 內部查詢專用之 Row 物件。</summary>
    private class MembershipRow
    {
        public int Id { get; init; }
        public int MerchantId { get; init; }
        public int UserId { get; init; }
        public int? RoleId { get; init; }
        public bool IsActive { get; init; }
        public bool IsOwner { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
