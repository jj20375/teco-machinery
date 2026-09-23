using System.Security.Cryptography;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// 管理員幫別人重設密碼時用——場館管理員重設自己場館成員、平台管理員重設任一場館成員，
/// 兩邊共用同一套產生規則，抽出來避免兩個 Endpoints 檔案各寫一份。
/// </summary>
public static class TemporaryPasswordGenerator
{
    /// <summary>符合登入頁密碼規則（8~16 碼、含英文字母＋數字）的隨機臨時密碼，固定 12 碼。</summary>
    public static string Generate()
    {
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
