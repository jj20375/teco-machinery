using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 平台系統帳號列表查詢專用 Row 物件 (<see cref="PlatformSystemUserRow"/>)。
/// 刻意排除 PasswordHash 欄位，確保 API 回傳資料時不會將安全雜湊外洩至前端。
/// </summary>
public sealed class PlatformSystemUserRow
{
    /// <summary>使用者主鍵 ID。</summary>
    public int Id { get; init; }
    /// <summary>帳號名稱。</summary>
    public required string Username { get; init; }
    /// <summary>中文顯示名稱。</summary>
    public required string DisplayName { get; init; }
    /// <summary>電子郵件信箱。</summary>
    public string? Email { get; init; }
    /// <summary>帳號是否啟用。</summary>
    public bool IsActive { get; init; }
    /// <summary>指派之平台角色 ID。</summary>
    public int? SystemRoleId { get; init; }
    /// <summary>指派之平台角色中文名稱。</summary>
    public string? RoleName { get; init; }
    /// <summary>是否為平台超級管理員。</summary>
    public bool IsPlatformAdmin { get; init; }
    /// <summary>最後成功登入時間 (UTC)。</summary>
    public DateTimeOffset? LastLoginAt { get; init; }
    /// <summary>帳號鎖定截止時間 (UTC)。</summary>
    public DateTimeOffset? LockedUntil { get; init; }
    /// <summary>帳號建立時間 (UTC)。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 系統使用者帳號資料庫倉儲 (<see cref="UserRepository"/>)。
/// 
/// 負責 app_user 資料表之存取操作。
/// 支援系統管理員查詢、依帳號名稱或 ID 查詢、新增帳號、登入驗證計數與鎖定管理、密碼更新、顯示名稱修改以及 AuthVersion 授權版本控制。
/// </summary>
public sealed class UserRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 查詢具備平台管理權限之系統帳號清單（SystemRoleId 非空或 IsPlatformAdmin = 1）。
    /// </summary>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>平台系統帳號 Row 物件清單。</returns>
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

    /// <summary>
    /// 新增一位全新使用者帳號主檔。
    /// </summary>
    /// <param name="user">欲建立之使用者實體物件。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>新增成功後之資料庫自動遞增主鍵 ID。</returns>
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

    /// <summary>
    /// 依據登入帳號名稱（不分大小寫）查詢使用者帳號實體。
    /// </summary>
    /// <param name="username">登入帳號名稱。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>使用者實體物件；若不存在則回傳 null。</returns>
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

    /// <summary>
    /// 依據使用者主鍵 ID 查詢使用者帳號實體。
    /// </summary>
    /// <param name="id">使用者主鍵 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>使用者實體物件；若不存在則回傳 null。</returns>
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

    /// <summary>
    /// 登記登入失敗：累計失敗計數，若達到鎖定門檻則設定帳號鎖定截止時間。
    /// </summary>
    /// <param name="userId">目標使用者 ID。</param>
    /// <param name="maxFailuresBeforeLock">觸發鎖定的連續失敗次數門檻。</param>
    /// <param name="lockDuration">鎖定期間長度。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>若本次失敗導致帳號觸發鎖定則回傳 true；若尚未達鎖定門檻回傳 false。</returns>
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

    /// <summary>
    /// 登記登入成功：重設失敗計數為 0、清空鎖定時間並更新最後登入時間。
    /// </summary>
    /// <param name="userId">使用者 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task RegisterLoginSuccessAsync(int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE app_user SET failed_login_count = 0, locked_until = NULL, last_login_at = @now WHERE id = @userId",
            new { userId, now = DateTimeOffset.UtcNow.UtcDateTime });
    }

    /// <summary>
    /// 更新使用者的密碼雜湊值。
    /// </summary>
    /// <param name="userId">使用者 ID。</param>
    /// <param name="passwordHash">安全雜湊後的全新密碼字串。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task UpdatePasswordAsync(int userId, string passwordHash, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE app_user SET password_hash = @passwordHash WHERE id = @userId", new { userId, passwordHash });
    }

    /// <summary>
    /// 修改使用者的中文顯示名稱。
    /// </summary>
    /// <param name="userId">使用者 ID。</param>
    /// <param name="displayName">全新中文顯示名稱。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task UpdateDisplayNameAsync(int userId, string displayName, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE app_user SET display_name = @displayName WHERE id = @userId", new { userId, displayName });
    }

    /// <summary>
    /// 遞增單一使用者的授權版本號 (auth_version + 1)。
    /// 當密碼修改、角色變更或帳號停用時調用，使該使用者先前簽發的所有 JWT 即刻失效。
    /// </summary>
    /// <param name="userId">使用者 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task IncrementAuthVersionAsync(int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE app_user SET auth_version = auth_version + 1 WHERE id = @userId", new { userId });
    }

    /// <summary>
    /// 手動解除使用者的登入失敗鎖定狀態。
    /// 管理員重設密碼或手動解鎖時調用。
    /// </summary>
    /// <param name="userId">使用者 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task ClearLockoutAsync(int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE app_user SET failed_login_count = 0, locked_until = NULL WHERE id = @userId", new { userId });
    }

    /// <summary>
    /// 批次遞增指定場館下所有成員之授權版本號。
    /// 當該場館之 CRUD 或子選項簡化開關變更時調用，確保該場館所有在線使用者的舊 Token 全數失效並強制重新簽發。
    /// </summary>
    /// <param name="merchantId">目標場館 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
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

    /// <summary>內部資料映射轉換方法。</summary>
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

    /// <summary>Dapper 內部查詢專用之 Row 物件。</summary>
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
