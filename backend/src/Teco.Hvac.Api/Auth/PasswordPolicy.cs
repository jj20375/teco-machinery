namespace Teco.Hvac.Api.Auth;

/// <summary>
/// 密碼規則的唯一來源：修改自己的密碼、建立帳號時的初始密碼都走這裡，前端的提示與檢查要跟這裡一致。
/// 只要求長度（最少 6 個字元，不強制英文＋數字）；上限 128 只是擋掉不合理的超長輸入，不是業務規則。
/// 管理員重設密碼產生的臨時密碼（TemporaryPasswordGenerator）固定 12 碼，天然符合。
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 6;
    public const int MaxLength = 128;

    public const string RuleText = "密碼至少要 6 個字元。";

    /// <summary>回傳 null 表示合格；否則是要直接顯示給使用者的原因。</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return RuleText;
        if (password.Length > MaxLength) return $"密碼最多 {MaxLength} 個字元。";
        return null;
    }
}
