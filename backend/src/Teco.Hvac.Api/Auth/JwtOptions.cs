namespace Teco.Hvac.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    /// <summary>至少 32 bytes（HS256 建議）。正式環境務必透過環境變數/secret 覆寫，不要用 appsettings 預設值。</summary>
    public required string SigningKey { get; init; }
    public int AccessTokenMinutes { get; init; } = 30;
    public int RefreshTokenDays { get; init; } = 14;
}
