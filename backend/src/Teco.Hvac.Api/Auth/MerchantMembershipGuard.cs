using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// 場館成員管理操作動作類型。
/// </summary>
public enum MemberAction
{
    /// <summary>刪除成員。</summary>
    Delete,
    /// <summary>停用成員帳號。</summary>
    Deactivate,
    /// <summary>重新啟用成員帳號。</summary>
    Activate,
    /// <summary>變更成員指派之角色。</summary>
    ChangeRole,
    /// <summary>重設成員密碼。</summary>
    ResetPassword,
    /// <summary>修改成員顯示名稱。</summary>
    Rename
}

/// <summary>
/// 場館成員管理的層級保護：避免一般管理員刪掉、停用、降級別的管理員，或幫他重設密碼後接管帳號
/// （只擋刪除不夠：能重設密碼就能登入成對方）。
/// - 擁有者（場館第一位管理員）：不能被刪除、停用、改角色；別人不能幫他重設密碼或改名，只有他本人能。
/// - 其他場館管理員：只有擁有者能刪除、停用、改角色、重設密碼、改名；本人改自己的名字／密碼不受限。
/// - 一般成員：沒有額外限制（原本的 merchant.users 權限檢查照舊）。
/// - 任何人都不能調整自己的權限、停用自己：手滑拿掉自己的「使用者管理」就再也改不回來（2026-10-19）。改名、改密碼不受限。
/// 平台管理員走 /api/v1/platform，不經過這裡，仍可處理擁有者的密碼重設。
/// </summary>
public static class MerchantMembershipGuard
{
    /// <summary>場館管理員的標準角色代碼 ("merchant-admin")。</summary>
    public const string AdminRoleCode = "merchant-admin";

    /// <summary>
    /// 檢查呼叫端是否有權限對目標成員執行特定操作。
    /// 回傳 null 表示可以做；否則是要直接回給前端顯示的原因（HTTP 403，body 帶 message）。
    /// </summary>
    /// <param name="scope">當前呼叫端之 RequestScope。</param>
    /// <param name="target">被操作之場館成員記錄。</param>
    /// <param name="action">預計執行的管理操作。</param>
    /// <param name="memberships">成員關聯倉儲。</param>
    /// <param name="roles">角色倉儲。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>若允許操作則為 null；若違反保護規則則回傳繁體中文錯誤原因字串。</returns>
    public static async Task<string?> CheckAsync(
        RequestScope scope, MerchantMembership target, MemberAction action,
        MembershipRepository memberships, RoleRepository roles, CancellationToken ct)
    {
        if (target.UserId == scope.UserId && action is MemberAction.ChangeRole or MemberAction.Deactivate)
            return action == MemberAction.ChangeRole
                ? "無法調整自己的權限，請由其他有「使用者管理」權限的人調整。"
                : "無法停用自己的帳號，請由其他有「使用者管理」權限的人處理。";

        // 判定目標對象是否為場館管理員角色
        var targetIsAdmin = false;
        if (target.RoleId is int roleId)
            targetIsAdmin = (await roles.FindByIdAsync(roleId, ct))?.Code == AdminRoleCode;
        // 如果目標不是擁有者也不是管理員，則不需要本守衛額外攔截
        if (!target.IsOwner && !targetIsAdmin) return null;

        var isSelf = target.UserId == scope.UserId;
        // 目標為擁有者時的保護原則
        if (target.IsOwner)
        {
            switch (action)
            {
                case MemberAction.Delete: return "這是場館的擁有者（第一位管理員），無法刪除。";
                case MemberAction.Deactivate: return "這是場館的擁有者（第一位管理員），無法停用。";
                case MemberAction.ChangeRole: return "這是場館的擁有者（第一位管理員），無法變更角色。";
                default:
                    return isSelf ? null : "這是場館的擁有者（第一位管理員），只有本人可以變更。";
            }
        }

        // 目標為一般管理員時，只有場館擁有者或本人特定操作可放行
        var caller = scope.MerchantId is int merchantId ? await memberships.FindAsync(merchantId, scope.UserId, ct) : null;
        if (caller?.IsOwner == true) return null;
        if (isSelf && action is MemberAction.ResetPassword or MemberAction.Rename) return null;

        return $"這是場館管理員的帳號，只有場館擁有者可以{Verb(action)}。";
    }

    /// <summary>
    /// 將操作類型列舉轉換為繁體中文動作動詞。
    /// </summary>
    /// <param name="action">成員操作動作。</param>
    /// <returns>繁體中文動詞文字。</returns>
    public static string Verb(MemberAction action) => action switch
    {
        MemberAction.Delete => "刪除", MemberAction.Deactivate => "停用", MemberAction.Activate => "啟用",
        MemberAction.ChangeRole => "變更角色", MemberAction.ResetPassword => "重設密碼", _ => "修改名稱",
    };

    /// <summary>
    /// 產生統一的 HTTP 403 拒絕存取 JSON 回應結果。
    /// </summary>
    /// <param name="message">錯誤提示訊息。</param>
    /// <returns>帶有 JSON 格式 message 的 IResult 物件。</returns>
    public static IResult Denied(string message) => Results.Json(new { message }, statusCode: StatusCodes.Status403Forbidden);
}
