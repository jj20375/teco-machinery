using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// Dapper 專用的 Refresh Token 資料列映射模型。
/// class＋init 屬性、時間欄位用 DateTime 不是 DateTimeOffset——理由見 CLAUDE.md 原則 1
/// 的重大 bug 記錄（DateTime→DateTimeOffset 的自動轉換會套用容器系統時區）。
/// </summary>
public sealed class RefreshTokenRow
{
    /// <summary>權杖流水號 PK。</summary>
    public required long Id { get; init; }
    /// <summary>所屬使用者 ID。</summary>
    public required int UserId { get; init; }
    /// <summary>SHA-256 雜湊後的權杖值。</summary>
    public required string TokenHash { get; init; }
    /// <summary>簽發時的使用者認證版本號（對齊 app_user.auth_version）。</summary>
    public required int AuthVersionAtIssue { get; init; }
    /// <summary>權杖有效截止時間戳記 (UTC)。</summary>
    public required DateTime ExpiresAtUtc { get; init; }
    /// <summary>權杖撤銷時間戳記 (UTC)；null 表示有效。</summary>
    public DateTime? RevokedAtUtc { get; init; }
}

/// <summary>
/// Refresh Token 資料存取倉儲。
/// 負責 Refresh Token 之建立、主動驗證檢查與批量撤銷。
/// </summary>
/// <param name="factory">資料庫連線工廠，用於管理 MariaDB 連線生命週期。</param>
public sealed class RefreshTokenRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 新增一筆 Refresh Token 記錄。
    /// </summary>
    /// <param name="userId">擁有此權杖的使用者 ID。</param>
    /// <param name="tokenHash">權杖原始字串經 SHA-256 雜湊之結果。</param>
    /// <param name="authVersionAtIssue">簽發當下之使用者認證版本號。</param>
    /// <param name="expiresAtUtc">到期時間 (UTC)。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>新增之資料庫流水號 PK。</returns>
    public async Task<long> CreateAsync(
        int userId, string tokenHash, int authVersionAtIssue, DateTimeOffset expiresAtUtc, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO refresh_token (user_id, token_hash, auth_version_at_issue, expires_at)
            VALUES (@userId, @tokenHash, @authVersionAtIssue, @expiresAt);
            SELECT LAST_INSERT_ID();
            """,
            new { userId, tokenHash, authVersionAtIssue, expiresAt = expiresAtUtc.UtcDateTime });
        return id;
    }

    /// <summary>
    /// 依權杖雜湊值尋找有效（未過期且未被撤銷）的 Refresh Token。
    /// 找一顆「還能用」的 refresh token：沒過期、沒被撤銷。不檢查 auth_version——那個交給呼叫端比對，這裡只負責查表面。
    /// </summary>
    /// <param name="tokenHash">待查驗的權杖 SHA-256 雜湊。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>找到之 <see cref="RefreshToken"/> 實體；若過期、被撤銷或不存在則為 null。</returns>
    public async Task<RefreshToken?> FindActiveAsync(string tokenHash, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RefreshTokenRow>(
            """
            SELECT id AS Id, user_id AS UserId, token_hash AS TokenHash,
                   auth_version_at_issue AS AuthVersionAtIssue, expires_at AS ExpiresAtUtc, revoked_at AS RevokedAtUtc
            FROM refresh_token
            WHERE token_hash = @tokenHash AND revoked_at IS NULL AND expires_at > UTC_TIMESTAMP(3)
            """,
            new { tokenHash });
        if (row is null) return null;
        return new RefreshToken
        {
            Id = row.Id, UserId = row.UserId, TokenHash = row.TokenHash,
            AuthVersionAtIssue = row.AuthVersionAtIssue,
            ExpiresAtUtc = row.ExpiresAtUtc.AsUtcOffset(), RevokedAtUtc = row.RevokedAtUtc.AsUtcOffset(),
        };
    }

    /// <summary>
    /// 撤銷單一指定的 Refresh Token。
    /// </summary>
    /// <param name="id">權杖資料庫流水號 PK。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    public async Task RevokeAsync(long id, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE refresh_token SET revoked_at = UTC_TIMESTAMP(3) WHERE id = @id AND revoked_at IS NULL",
            new { id });
    }

    /// <summary>
    /// 密碼變更、帳號停用等場景一次撤銷這個使用者名下全部尚未撤銷的 refresh token。
    /// </summary>
    /// <param name="userId">目標使用者識別碼。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    public async Task RevokeAllForUserAsync(int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE refresh_token SET revoked_at = UTC_TIMESTAMP(3) WHERE user_id = @userId AND revoked_at IS NULL",
            new { userId });
    }
}
