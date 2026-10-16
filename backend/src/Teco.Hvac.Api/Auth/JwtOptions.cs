namespace Teco.Hvac.Api.Auth;

/// <summary>
/// JWT 驗證與權杖簽發組態選項。
/// </summary>
public sealed class JwtOptions
{
    /// <summary>組態設定檔中的對應區段名稱 ("Jwt")。</summary>
    public const string SectionName = "Jwt";

    /// <summary>JWT 發行者名稱 (Issuer)。</summary>
    public required string Issuer { get; init; }

    /// <summary>JWT 接收對象 (Audience)。</summary>
    public required string Audience { get; init; }

    /// <summary>至少 32 bytes（HS256 建議）。正式環境務必透過環境變數/secret 覆寫，不要用 appsettings 預設值。</summary>
    public required string SigningKey { get; init; }

    /// <summary>Access Token 有效時間（分鐘），預設為 30 分鐘。</summary>
    public int AccessTokenMinutes { get; init; } = 30;

    /// <summary>Refresh Token 有效時間（天數），預設為 14 天。</summary>
    public int RefreshTokenDays { get; init; } = 14;
}
