using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// class＋init 屬性、時間欄位用 DateTime 不是 DateTimeOffset——理由見 CLAUDE.md 原則 1
/// 的重大 bug 記錄（DateTime→DateTimeOffset 的自動轉換會套用容器系統時區）。
/// </summary>
public sealed class RefreshTokenRow
{
    public required long Id { get; init; }
    public required int UserId { get; init; }
    public required string TokenHash { get; init; }
    public required int AuthVersionAtIssue { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public DateTime? RevokedAtUtc { get; init; }
}

public sealed class RefreshTokenRepository(TecoDbConnectionFactory factory)
{
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

    /// <summary>找一顆「還能用」的 refresh token：沒過期、沒被撤銷。不檢查 auth_version——那個交給呼叫端比對，這裡只負責查表面。</summary>
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

    public async Task RevokeAsync(long id, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE refresh_token SET revoked_at = UTC_TIMESTAMP(3) WHERE id = @id AND revoked_at IS NULL",
            new { id });
    }

    /// <summary>密碼變更、帳號停用等場景一次撤銷這個使用者名下全部尚未撤銷的 refresh token。</summary>
    public async Task RevokeAllForUserAsync(int userId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE refresh_token SET revoked_at = UTC_TIMESTAMP(3) WHERE user_id = @userId AND revoked_at IS NULL",
            new { userId });
    }
}
