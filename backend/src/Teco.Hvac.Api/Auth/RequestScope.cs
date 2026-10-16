using System.Security.Claims;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// 從已驗證 JWT 解析出的使用者、場館與權限範圍。
/// 共用檢查形式：<c>scope.Has(code, action)</c> 與 <c>scope.HasOption(code, option)</c>；
/// 前端隱藏按鈕不能取代這裡的檢查——每個寫入端點都要再驗證一次。
/// </summary>
/// <param name="UserId">目前登入者的使用者識別碼 (sub)。</param>
/// <param name="MerchantId">當前鎖定的場館識別碼；若為平台總管範圍則可能為 null。</param>
/// <param name="ScopeKind">範圍類型 ("platform" 或 "merchant")。</param>
/// <param name="IsPlatformAdmin">是否為系統平台超級管理員。</param>
/// <param name="Permissions">擁有的功能權限代碼集合（格式為 "資源代碼:動作"，例如 "device.fcu:write"）。</param>
/// <param name="Options">擁有的選項/屬性層級權限集合（例如 "chiller.setting:control"）。</param>
/// <param name="AuthVersion">簽發此權杖當下的使用者認證版本號（用於即時比對密碼重設或停用狀態）。</param>
public sealed record RequestScope(
    int UserId,
    int? MerchantId,
    string ScopeKind,
    bool IsPlatformAdmin,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<string> Options,
    int AuthVersion)
{
    /// <summary>判斷目前請求範圍是否為全域平台層級 (platform scope)。</summary>
    public bool IsPlatform => string.Equals(ScopeKind, "platform", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 嘗試從 HTTP 請求的 ClaimsPrincipal 解析出結構化的 RequestScope。
    /// </summary>
    /// <param name="user">HTTP 請求的使用者宣告主體 (HttpContext.User)。</param>
    /// <param name="scope">解析成功時回傳的 RequestScope 實體；若失敗則為 null。</param>
    /// <returns>若宣告齊全且格式正確則為 true；否則為 false。</returns>
    public static bool TryRead(ClaimsPrincipal user, out RequestScope? scope)
    {
        scope = null;
        // 必須包含合法的使用者流水號 (sub)
        if (!int.TryParse(user.FindFirstValue("sub"), out var userId)) return false;

        // 解析範圍類型與場館 ID
        var scopeKind = user.FindFirstValue("scope_kind") ?? "platform";
        var merchantId = int.TryParse(user.FindFirstValue("merchant_id"), out var parsedMerchantId) ? parsedMerchantId : (int?)null;
        // 若宣告為場館範圍，則必須帶有有效的場館 ID
        if (string.Equals(scopeKind, "merchant", StringComparison.OrdinalIgnoreCase) && merchantId is null) return false;

        _ = int.TryParse(user.FindFirstValue("auth_version"), out var authVersion);
        var isPlatformAdmin = string.Equals(user.FindFirstValue("is_platform_admin"), "true", StringComparison.OrdinalIgnoreCase);

        // 組裝權限宣告集合
        scope = new RequestScope(
            userId,
            merchantId,
            scopeKind,
            isPlatformAdmin,
            user.FindAll("permission").Select(c => c.Value).ToHashSet(StringComparer.Ordinal),
            user.FindAll("permission_option").Select(c => c.Value).ToHashSet(StringComparer.Ordinal),
            authVersion);
        return true;
    }

    /// <summary>
    /// 檢查當前請求範圍是否擁有指定資源與動作的權限。
    /// </summary>
    /// <param name="code">權限資源代碼（例如 "device.chiller"）。</param>
    /// <param name="action">動作名稱（例如 "read", "write"）。</param>
    /// <returns>若具備指定權限或具備全域萬用字元 "*" 則回傳 true；否則為 false。</returns>
    public bool Has(string code, string action) =>
        Permissions.Contains($"{code}:{action}") || Permissions.Contains("*");

    /// <summary>
    /// 檢查當前請求範圍是否擁有指定資源與進階選項的權限。
    /// </summary>
    /// <param name="code">權限資源代碼。</param>
    /// <param name="option">選項名稱（例如 "remote_control"）。</param>
    /// <returns>若具備指定選項或具備全域萬用字元 "*" 則回傳 true；否則為 false。</returns>
    public bool HasOption(string code, string option) =>
        Options.Contains($"{code}:{option}") || Options.Contains("*");
}
