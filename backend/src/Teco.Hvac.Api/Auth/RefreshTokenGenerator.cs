using System.Security.Cryptography;
using System.Text;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// Refresh token 是隨機的不透明字串，不像 JWT 一樣自解釋——資料庫只存它的 SHA-256 hash，
/// 明文只在簽發當下回給前端一次，就算資料庫外洩也拿不到能直接使用的 token（跟密碼雜湊
/// 同一個防禦思路，只是這裡用雜湊比對而非慢雜湊，因為 token 本身熵夠高不怕暴力猜測）。
/// </summary>
public static class RefreshTokenGenerator
{
    public static string GeneratePlainToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string plainToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
