using System.Security.Claims;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// 從已驗證 JWT 解析出的使用者、場館與權限範圍（比照美達特 RequestScope）。
/// 共用檢查形式：<c>scope.Has(code, action)</c> 與 <c>scope.HasOption(code, option)</c>；
/// 前端隱藏按鈕不能取代這裡的檢查——每個寫入端點都要再驗證一次。
/// </summary>
public sealed record RequestScope(
    int UserId,
    int? MerchantId,
    string ScopeKind,
    bool IsPlatformAdmin,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<string> Options,
    int AuthVersion)
{
    public bool IsPlatform => string.Equals(ScopeKind, "platform", StringComparison.OrdinalIgnoreCase);

    public static bool TryRead(ClaimsPrincipal user, out RequestScope? scope)
    {
        scope = null;
        if (!int.TryParse(user.FindFirstValue("sub"), out var userId)) return false;

        var scopeKind = user.FindFirstValue("scope_kind") ?? "platform";
        var merchantId = int.TryParse(user.FindFirstValue("merchant_id"), out var parsedMerchantId) ? parsedMerchantId : (int?)null;
        if (string.Equals(scopeKind, "merchant", StringComparison.OrdinalIgnoreCase) && merchantId is null) return false;

        _ = int.TryParse(user.FindFirstValue("auth_version"), out var authVersion);
        var isPlatformAdmin = string.Equals(user.FindFirstValue("is_platform_admin"), "true", StringComparison.OrdinalIgnoreCase);

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

    public bool Has(string code, string action) =>
        Permissions.Contains($"{code}:{action}") || Permissions.Contains("*");

    public bool HasOption(string code, string option) =>
        Options.Contains($"{code}:{option}") || Options.Contains("*");
}
