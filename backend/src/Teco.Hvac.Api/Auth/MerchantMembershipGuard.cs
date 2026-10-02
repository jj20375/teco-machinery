using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Auth;

public enum MemberAction { Delete, Deactivate, Activate, ChangeRole, ResetPassword, Rename }

/// <summary>
/// 場館成員管理的層級保護：避免一般管理員刪掉、停用、降級別的管理員，或幫他重設密碼後接管帳號
/// （只擋刪除不夠：能重設密碼就能登入成對方）。
/// - 擁有者（場館第一位管理員）：不能被刪除、停用、改角色；別人不能幫他重設密碼或改名，只有他本人能。
/// - 其他場館管理員：只有擁有者能刪除、停用、改角色、重設密碼、改名；本人改自己的名字／密碼不受限。
/// - 一般成員：沒有額外限制（原本的 merchant.users 權限檢查照舊）。
/// 平台管理員走 /api/v1/platform，不經過這裡，仍可處理擁有者的密碼重設。
/// </summary>
public static class MerchantMembershipGuard
{
    public const string AdminRoleCode = "merchant-admin";

    /// <summary>回傳 null 表示可以做；否則是要直接回給前端顯示的原因（HTTP 403，body 帶 message）。</summary>
    public static async Task<string?> CheckAsync(
        RequestScope scope, MerchantMembership target, MemberAction action,
        MembershipRepository memberships, RoleRepository roles, CancellationToken ct)
    {
        var targetIsAdmin = false;
        if (target.RoleId is int roleId)
            targetIsAdmin = (await roles.FindByIdAsync(roleId, ct))?.Code == AdminRoleCode;
        if (!target.IsOwner && !targetIsAdmin) return null;

        var isSelf = target.UserId == scope.UserId;
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

        var caller = scope.MerchantId is int merchantId ? await memberships.FindAsync(merchantId, scope.UserId, ct) : null;
        if (caller?.IsOwner == true) return null;
        if (isSelf && action is MemberAction.ResetPassword or MemberAction.Rename) return null;

        var verb = action switch
        {
            MemberAction.Delete => "刪除", MemberAction.Deactivate => "停用", MemberAction.Activate => "啟用", MemberAction.ChangeRole => "變更角色",
            MemberAction.ResetPassword => "重設密碼", _ => "修改",
        };
        return $"這是場館管理員的帳號，只有場館擁有者可以{verb}。";
    }

    public static IResult Denied(string message) => Results.Json(new { message }, statusCode: StatusCodes.Status403Forbidden);
}
