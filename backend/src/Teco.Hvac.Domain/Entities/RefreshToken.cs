namespace Teco.Hvac.Domain.Entities;

/// <summary>存的是 SHA-256 hash，不是明文 token——明文只在簽發當下回給前端一次。</summary>
public sealed class RefreshToken
{
    public required long Id { get; init; }
    public required int UserId { get; init; }
    public required string TokenHash { get; init; }
    public required int AuthVersionAtIssue { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
}
