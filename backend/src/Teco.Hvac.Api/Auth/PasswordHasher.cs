using System.Security.Cryptography;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// PBKDF2-HMACSHA256 密碼雜湊，不引入額外套件（BCrypt/Identity）。
/// 格式："{iterations}.{saltBase64}.{hashBase64}"，驗證時重算比對，不儲存明文或可逆密文。
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000; // OWASP 2023+ 建議下限
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations)) return false;

        var salt = Convert.FromBase64String(parts[1]);
        var expectedHash = Convert.FromBase64String(parts[2]);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
