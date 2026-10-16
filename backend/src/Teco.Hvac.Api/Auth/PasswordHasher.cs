using System.Security.Cryptography;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// PBKDF2-HMACSHA256 密碼雜湊，不引入額外套件（BCrypt/Identity）。
/// 格式："{iterations}.{saltBase64}.{hashBase64}"，驗證時重算比對，不儲存明文或可逆密文。
/// </summary>
public static class PasswordHasher
{
    /// <summary>PBKDF2 疊代次數（OWASP 2023+ 建議下限 210,000 次）。</summary>
    private const int Iterations = 210_000;
    /// <summary>密碼鹽 (Salt) 長度（16 位元組）。</summary>
    private const int SaltSize = 16;
    /// <summary>衍生金鑰雜湊長度（32 位元組，256 位元）。</summary>
    private const int HashSize = 32;

    /// <summary>
    /// 將明文密碼透過隨機 Salt 與 PBKDF2 演算法計算雜湊字串。
    /// </summary>
    /// <param name="password">明文密碼。</param>
    /// <returns>格式為 "{iterations}.{saltBase64}.{hashBase64}" 的安全儲存字串。</returns>
    public static string Hash(string password)
    {
        // 產生密碼學安全等級的隨機鹽值
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        // 使用 PBKDF2-HMACSHA256 衍生金鑰雜湊
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        // 格式化輸出
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// 比對明文密碼與資料庫儲存的雜湊字串是否相符。
    /// </summary>
    /// <param name="password">使用者輸入的明文密碼。</param>
    /// <param name="encoded">先前由 <see cref="Hash"/> 產生的編碼雜湊字串。</param>
    /// <returns>若密碼完全正確則為 true；否則為 false。</returns>
    public static bool Verify(string password, string encoded)
    {
        // 拆解格式為 [疊代次數, 鹽, 雜湊值]
        var parts = encoded.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations)) return false;

        // 還原原始二進位鹽值與預期雜湊
        var salt = Convert.FromBase64String(parts[1]);
        var expectedHash = Convert.FromBase64String(parts[2]);
        // 使用相同參數重新衍生雜湊
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);

        // 使用固定時間比較運算，防止時序攻擊 (Timing Attack)
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
