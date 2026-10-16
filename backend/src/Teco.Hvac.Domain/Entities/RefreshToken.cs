namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 憑證重新整理權杖 (Refresh Token) 資料庫實體 (<see cref="RefreshToken"/>)。
/// 對應資料庫 refresh_token 資料表。
/// 
/// 【安全性防護】
/// 資料庫內部僅儲存 SHA-256 雜湊後的 TokenHash，不以明文儲存；
/// 原始明文隨機字串僅於登入或換票當下單向回傳給前端客戶端儲存。
/// </summary>
public sealed class RefreshToken
{
    /// <summary>權杖紀錄主鍵識別碼 (Primary Key)。</summary>
    public required long Id { get; init; }

    /// <summary>所屬之使用者 ID。</summary>
    public required int UserId { get; init; }

    /// <summary>隨機權杖之 SHA-256 雜湊字串。</summary>
    public required string TokenHash { get; init; }

    /// <summary>簽發該權杖當下使用者之授權版本號 (AuthVersion)；若使用者密碼變更或權限重設，將導致版本不符而自動作廢。</summary>
    public required int AuthVersionAtIssue { get; init; }

    /// <summary>權杖之過期失效標準 UTC 時間戳記。</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>權杖被手動撤銷註銷之標準 UTC 時間戳記；若尚未被撤銷則為 null。</summary>
    public DateTimeOffset? RevokedAtUtc { get; init; }
}
