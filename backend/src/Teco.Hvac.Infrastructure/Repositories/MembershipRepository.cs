using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 場館成員清單一列，已 JOIN 使用者與角色資料，供 /api/v1/merchant/users 直接回傳。
/// 刻意用一般 class＋init 屬性，不用 positional record——Dapper 對 record 主建構式的型別比對
/// 很嚴格（要跟底層 ADO.NET 回傳的原始型別如 uint/sbyte 完全一致），容易在執行期才炸開；
/// class＋init 屬性走的是「建物件後用屬性名稱逐一賦值」這條路，跟本檔案其他 Row 類別一致。
/// </summary>
public sealed class MerchantUserRow
{
    public int MembershipId { get; init; }
    public int UserId { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public bool IsActive { get; init; }
    public bool IsOwner { get; init; }
    public int? RoleId { get; init; }
    public string? RoleCode { get; init; }
    public string? RoleName { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }
}

public sealed class MembershipRepository(TecoDbConnectionFactory factory)
{
    /// <summary>取得使用者所有生效中的場館成員資格（一人可掛多個場館）。</summary>
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
    /// 刪掉的是「這個人在這個場館的成員資格」，不是 app_user 這個帳號本身——同一個人理論上
    /// 可以是多個場館的成員（雖然目前場館端點都只處理自己場館），刪掉 membership 不影響
    /// 這個人在其他場館的資格，也不影響帳號本身能不能登入（純平台帳號、其他場館成員資格）。
    /// </summary>
    public async Task DeleteAsync(int membershipId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM merchant_membership WHERE id = @membershipId", new { membershipId });
    }

    public async Task<bool> HasOwnerAsync(int merchantId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM merchant_membership WHERE merchant_id = @merchantId AND is_owner = 1", new { merchantId }) > 0;
    }

    /// <summary>刪除角色前的擋板用——還有人在用這個角色就不給刪，避免這些成員突然失去所有權限。</summary>
    public async Task<int> CountByRoleAsync(int roleId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM merchant_membership WHERE role_id = @roleId", new { roleId });
    }

    private static MerchantMembership ToEntity(MembershipRow r) => new()
    {
        Id = r.Id, MerchantId = r.MerchantId, UserId = r.UserId, RoleId = r.RoleId,
        IsActive = r.IsActive, IsOwner = r.IsOwner, CreatedAt = r.CreatedAt,
    };

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
