using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 平台系統帳號清單專用（`GET /api/v1/platform/system-users`）。刻意不重用 AppUser 實體直接
/// 序列化——AppUser.PasswordHash 是 public 屬性，直接 Results.Ok(AppUser 清單) 會把 PBKDF2
/// 雜湊值＋鹽值整包送進 API 回應，即使雜湊過也不該讓前端拿到。跟 MembershipRepository 的
/// MerchantUserRow 是同一個理由、同一種修法。
/// </summary>
public sealed class PlatformSystemUserRow
{
    public int Id { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public bool IsActive { get; init; }
    public int? SystemRoleId { get; init; }
    public string? RoleName { get; init; }
    public bool IsPlatformAdmin { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class UserRepository(TecoDbConnectionFactory factory)
{
    /// <summary>平台系統帳號（system_role_id 非空或 is_platform_admin=1）清單，不含場館成員帳號。</summary>
    public async Task<IReadOnlyList<PlatformSystemUserRow>> ListSystemUsersAsync(CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<PlatformSystemUserRow>(
            """
            SELECT u.id AS Id, u.username AS Username, u.display_name AS DisplayName, u.email AS Email,
                   u.is_active AS IsActive, u.system_role_id AS SystemRoleId, r.name AS RoleName,
                   u.is_platform_admin AS IsPlatformAdmin, u.last_login_at AS LastLoginAt,
                   u.locked_until AS LockedUntil, u.created_at AS CreatedAt
            FROM app_user u
            LEFT JOIN app_role r ON r.id = u.system_role_id
            WHERE u.system_role_id IS NOT NULL OR u.is_platform_admin = 1
            ORDER BY u.created_at
            """);
        return rows.ToList();
    }

    public async Task<int> CreateAsync(AppUser user, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO app_user (username, display_name, email, password_hash, is_active, system_role_id, is_platform_admin)
            VALUES (@Username, @DisplayName, @Email, @PasswordHash, @IsActive, @SystemRoleId, @IsPlatformAdmin);
            SELECT LAST_INSERT_ID();
            """, user);
        return (int)id;
    }

    public async Task<AppUser?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            """
            SELECT id, username, display_name AS DisplayName, email AS Email, password_hash AS PasswordHash, is_active AS IsActive,
                   system_role_id AS SystemRoleId, is_platform_admin AS IsPlatformAdmin, auth_version AS AuthVersion,
                   failed_login_count AS FailedLoginCount, locked_until AS LockedUntil, last_login_at AS LastLoginAt,
                   created_at AS CreatedAt
            FROM app_user WHERE username = @username
            """, new { username });
        return row is null ? null : ToEntity(row);
    }

    public async Task<AppUser?> FindByIdAsync(int id, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            """
            SELECT id, username, display_name AS DisplayName, email AS Email, password_hash AS PasswordHash, is_active AS IsActive,
                   system_role_id AS SystemRoleId, is_platform_admin AS IsPlatformAdmin, auth_version AS AuthVersion,
                   failed_login_count AS FailedLoginCount, locked_until AS LockedUntil, last_login_at AS LastLoginAt,
                   created_at AS CreatedAt
            FROM app_user WHERE id = @id
            """, new { id });
        return row is null ? null : ToEntity(row);
    }

    /// <summary>登入失敗：累加失敗次數，達門檻鎖定帳號。回傳是否因此次失敗觸發鎖定。</summary>
    public async Task<bool> RegisterLoginFailureAsync(int userId, int maxFailuresBeforeLock, TimeSpan lockDuration, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var failedCount = await conn.ExecuteScalarAsync<int>(
            "SELECT failed_login_count FROM app_user WHERE id = @userId", new { userId });
        failedCount++;

        if (failedCount >= maxFailuresBeforeLock)
        {
            var lockedUntil = DateTimeOffset.UtcNow.Add(lockDuration);
            await conn.ExecuteAsync(
                "UPDATE app_user SET failed_login_count = 0, locked_until = @lockedUntil WHERE id = @userId",
                new { userId, lockedUntil = lockedUntil.UtcDateTime });
            return true;
        }

        await conn.ExecuteAsync("UPDATE app_user SET failed_login_count = @failedCount WHERE id = @userId", new { userId, failedCount });
        return false;
    }

    public async Task RegisterLoginSuccessAsync(int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE app_user SET failed_login_count = 0, locked_until = NULL, last_login_at = @now WHERE id = @userId",
            new { userId, now = DateTimeOffset.UtcNow.UtcDateTime });
    }

    public async Task UpdatePasswordAsync(int userId, string passwordHash, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE app_user SET password_hash = @passwordHash WHERE id = @userId", new { userId, passwordHash });
    }

    public async Task UpdateDisplayNameAsync(int userId, string displayName, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE app_user SET display_name = @displayName WHERE id = @userId", new { userId, displayName });
    }

    /// <summary>密碼、角色或場館設定變更時呼叫，遞增授權版本使舊 JWT 立即失效。</summary>
    public async Task IncrementAuthVersionAsync(int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE app_user SET auth_version = auth_version + 1 WHERE id = @userId", new { userId });
    }

    /// <summary>解除登入失敗鎖定（管理員重設密碼時一併呼叫，避免密碼改好了卻還卡在鎖定期）。</summary>
    public async Task ClearLockoutAsync(int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE app_user SET failed_login_count = 0, locked_until = NULL WHERE id = @userId", new { userId });
    }

    /// <summary>批次遞增：場館 CRUD/子項開關變更時，該場館所有會員的 JWT 都要失效。</summary>
    public async Task IncrementAuthVersionForMerchantAsync(int merchantId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            """
            UPDATE app_user u
            JOIN merchant_membership m ON m.user_id = u.id
            SET u.auth_version = u.auth_version + 1
            WHERE m.merchant_id = @merchantId
            """, new { merchantId });
    }

    private static AppUser ToEntity(UserRow r) => new()
    {
        Id = r.Id,
        Username = r.Username,
        DisplayName = r.DisplayName,
        Email = r.Email,
        PasswordHash = r.PasswordHash,
        IsActive = r.IsActive,
        SystemRoleId = r.SystemRoleId,
        IsPlatformAdmin = r.IsPlatformAdmin,
        AuthVersion = r.AuthVersion,
        FailedLoginCount = r.FailedLoginCount,
        LockedUntil = r.LockedUntil,
        LastLoginAt = r.LastLoginAt,
        CreatedAt = r.CreatedAt,
    };

    private sealed class UserRow
    {
        public int Id { get; init; }
        public required string Username { get; init; }
        public required string DisplayName { get; init; }
        public string? Email { get; init; }
        public required string PasswordHash { get; init; }
        public bool IsActive { get; init; }
        public int? SystemRoleId { get; init; }
        public bool IsPlatformAdmin { get; init; }
        public int AuthVersion { get; init; }
        public int FailedLoginCount { get; init; }
        public DateTimeOffset? LockedUntil { get; init; }
        public DateTimeOffset? LastLoginAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
