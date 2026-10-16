using System.Security.Cryptography;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// 臨時密碼產生器。
/// 管理員幫別人重設密碼時用——場館管理員重設自己場館成員、平台管理員重設任一場館成員，
/// 兩邊共用同一套產生規則，抽出來避免兩個 Endpoints 檔案各寫一份。
/// </summary>
public static class TemporaryPasswordGenerator
{
    /// <summary>
    /// 產生一組符合 PasswordPolicy（最少 6 碼）的隨機臨時密碼。
    /// 固定長度為 12 碼，混用英文字母（排除易混淆字元）與數字。
    /// </summary>
    /// <returns>長度 12 碼的隨機臨時密碼字串。</returns>
    public static string Generate()
    {
        // 排除容易視覺混淆的字元（如 0, O, 1, I, l 等）
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < chars.Length; i++)
        {
            var pool = i % 3 == 0 ? digits : letters; // 保證至少有數字，其餘以字母為主
            chars[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
        }
        return new string(chars);
    }
}
