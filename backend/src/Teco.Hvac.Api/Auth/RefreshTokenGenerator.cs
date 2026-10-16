using System.Security.Cryptography;
using System.Text;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// Refresh Token 產生與雜湊運算輔助工具。
/// Refresh token 是隨機的不透明字串，不像 JWT 一樣自解釋——資料庫只存它的 SHA-256 hash，
/// 明文只在簽發當下回給前端一次，就算資料庫外洩也拿不到能直接使用的 token（跟密碼雜湊
/// 同一個防禦思路，只是這裡用雜湊比對而非慢雜湊，因為 token 本身熵夠高不怕暴力猜測）。
/// </summary>
public static class RefreshTokenGenerator
{
    /// <summary>
    /// 產生高熵的 32 位元組隨機 Refresh Token 明文字串（Base64 URL 安全格式）。
    /// </summary>
    /// <returns>URL-safe Base64 編碼的隨機權杖明文。</returns>
    public static string GeneratePlainToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// 將 Refresh Token 明文進行 SHA-256 雜湊，產生適合存入資料庫的比對字串。
    /// </summary>
    /// <param name="plainToken">權杖明文字串。</param>
    /// <returns>64 字元的十六進位小寫雜湊字串。</returns>
    public static string Hash(string plainToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
