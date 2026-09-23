using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 平台管理端點：只有 platform scope 且持有對應 platform.* 權限的帳號能呼叫
/// （比照美達特 /api/platform/*）。場館自己的日常管理（自家帳號/角色）走 MerchantEndpoints。
/// </summary>
public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/platform").RequireAuthorization();

        group.MapGet("/merchants", ListMerchants);
        group.MapPost("/merchants", CreateMerchant);
        group.MapPatch("/merchants/{merchantId:int}", UpdateMerchant);
        group.MapPost("/merchants/{merchantId:int}/memberships", CreateMerchantMembership);
        group.MapPost("/merchants/{merchantId:int}/memberships/{membershipId:int}/reset-password", ResetMembershipPassword);

        group.MapGet("/system-users", ListSystemUsers);
        group.MapPost("/system-users", CreateSystemUser);

        group.MapGet("/roles", ListRoles);
        group.MapPost("/roles", CreateRole);
        group.MapGet("/roles/{roleId:int}/permissions", GetRolePermissions);
        group.MapPost("/roles/{roleId:int}/permissions", SetRolePermissions);

        group.MapGet("/permissions", ListPermissions);
    }

    private static bool RequirePlatform(ClaimsPrincipal principal, string code, string action, out RequestScope? scope)
    {
        scope = null;
        if (!RequestScope.TryRead(principal, out scope) || scope is null) return false;
        return scope.IsPlatform && scope.Has(code, action);
    }

    // ---- 場館 ----

    private static async Task<IResult> ListMerchants(ClaimsPrincipal principal, MerchantRepository repo, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.merchants", "read", out _)) return Results.Forbid();
        return Results.Ok(await repo.ListAsync(ct));
    }

    private static async Task<IResult> CreateMerchant(
        ClaimsPrincipal principal, CreateMerchantRequest request, MerchantRepository repo, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.merchants", "create", out var scope)) return Results.Forbid();
        var merchant = new Merchant
        {
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            IsRoleCrudConfigurationEnabled = request.IsRoleCrudConfigurationEnabled ?? true,
            IsRoleOptionConfigurationEnabled = request.IsRoleOptionConfigurationEnabled ?? true,
        };
        var id = await repo.CreateAsync(merchant, ct);

        await opLog.LogAsync(scope!, "platform.merchant.create", "merchant", id.ToString(),
            $"建立商家「{merchant.Name}」（{merchant.Code}）", after: merchant, ct: ct);
        return Results.Created($"/api/v1/platform/merchants/{id}", new { id });
    }

    /// <summary>
    /// 更新場館，含 CRUD/子項簡化開關——這正是「決定商家是否啟用 CRUD 與子項功能」的落地端點。
    /// 開關真的變動時遞增該場館所有成員的 AuthVersion，讓舊 JWT 立即失效、新模式馬上生效。
    /// </summary>
    private static async Task<IResult> UpdateMerchant(
        ClaimsPrincipal principal, int merchantId, UpdateMerchantRequest request,
        MerchantRepository merchants, UserRepository users, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.merchants", "update", out var scope)) return Results.Forbid();

        var before = await merchants.FindByIdAsync(merchantId, ct);
        var changed = await merchants.UpdateRolePermissionConfigurationAsync(
            merchantId, request.IsRoleCrudConfigurationEnabled, request.IsRoleOptionConfigurationEnabled, ct);
        if (changed) await users.IncrementAuthVersionForMerchantAsync(merchantId, ct);

        var merchant = await merchants.FindByIdAsync(merchantId, ct);
        if (merchant is null) return Results.NotFound();

        if (changed)
        {
            await opLog.LogAsync(scope!, "platform.merchant.update", "merchant", merchantId.ToString(),
                $"調整商家「{merchant.Name}」的 CRUD/子項權限開關",
                before: new { before?.IsRoleCrudConfigurationEnabled, before?.IsRoleOptionConfigurationEnabled },
                after: new { merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled }, ct: ct);
        }
        return Results.Ok(merchant);
    }

    private static async Task<IResult> CreateMerchantMembership(
        ClaimsPrincipal principal, int merchantId, CreateMembershipRequest request,
        MerchantRepository merchants, MembershipRepository memberships, RoleRepository roles, UserRepository users,
        OperationLogger opLog, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.merchants", "update", out var scope)) return Results.Forbid();
        var merchant = await merchants.FindByIdAsync(merchantId, ct);
        if (merchant is null) return Results.NotFound();

        var role = await roles.FindByIdAsync(request.RoleId, ct);
        if (role is null || role.Scope != RoleScope.Merchant) return Results.BadRequest(new { message = "角色不存在或不是場館範圍角色。" });

        var id = await memberships.CreateAsync(new MerchantMembership { MerchantId = merchantId, UserId = request.UserId, RoleId = request.RoleId }, ct);

        var targetUser = await users.FindByIdAsync(request.UserId, ct);
        await opLog.LogAsync(scope!, "platform.membership.create", "merchant_membership", id.ToString(),
            $"將使用者「{targetUser?.DisplayName ?? request.UserId.ToString()}」加入商家「{merchant.Name}」，角色：{role.Name}",
            after: new { MerchantId = merchantId, request.UserId, request.RoleId }, ct: ct);
        return Results.Created($"/api/v1/platform/merchants/{merchantId}/memberships/{id}", new { id });
    }

    /// <summary>
    /// 平台管理員重設任一場館成員的密碼——先前只有場館管理員能重設自己場館成員的密碼，
    /// 平台這邊完全沒有對應能力。邏輯比照 MerchantEndpoints.ResetUserPassword，差別只在
    /// 這裡不檢查 membership 屬於「呼叫者自己的場館」（平台管理員本來就能跨場館操作），
    /// 改成用 URL 上的 merchantId 核對 membership 真的屬於這個場館。
    ///
    /// 遞增 AuthVersion 只作用在被重設密碼的那個使用者身上，不會動到呼叫端（平台管理員）
    /// 自己的 AuthVersion——這是先前「編輯成員面板」踩過的真實 bug 的同一個教訓
    /// （改別人權限時不小心把整個場館／自己一起登出），這裡從一開始設計就避開。
    /// </summary>
    private static async Task<IResult> ResetMembershipPassword(
        ClaimsPrincipal principal, int merchantId, int membershipId, MerchantRepository merchants,
        MembershipRepository memberships, UserRepository users, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.IsPlatform) return Results.Forbid();
        if (!scope.Has("platform.merchants", "update") || !scope.HasOption("platform.merchants", "reset_password"))
            return Results.Forbid();

        var merchant = await merchants.FindByIdAsync(merchantId, ct);
        if (merchant is null) return Results.NotFound();

        var membership = await memberships.FindByIdAsync(membershipId, ct);
        if (membership is null || membership.MerchantId != merchantId) return Results.NotFound();

        var temporaryPassword = TemporaryPasswordGenerator.Generate();
        await users.UpdatePasswordAsync(membership.UserId, PasswordHasher.Hash(temporaryPassword), ct);
        await users.ClearLockoutAsync(membership.UserId, ct);
        await users.IncrementAuthVersionAsync(membership.UserId, ct);

        var targetUser = await users.FindByIdAsync(membership.UserId, ct);
        // 只記「已重設」這個事實，不記密碼本身——密碼永遠不進 operation_log。
        await opLog.LogAsync(scope, "platform.membership.reset_password", "merchant_membership", membershipId.ToString(),
            $"重設了商家「{merchant.Name}」成員「{targetUser?.DisplayName ?? $"membership#{membershipId}"}」的登入密碼", ct: ct);
        return Results.Ok(new { temporaryPassword });
    }

    // ---- 系統帳號（平台帳號） ----

    private static async Task<IResult> ListSystemUsers(ClaimsPrincipal principal, UserRepository users, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.system_users", "read", out _)) return Results.Forbid();
        return Results.Ok(await users.ListSystemUsersAsync(ct));
    }

    private static async Task<IResult> CreateSystemUser(
        ClaimsPrincipal principal, CreateSystemUserRequest request, UserRepository users, RoleRepository roles,
        OperationLogger opLog, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.system_users", "create", out var scope)) return Results.Forbid();

        var role = await roles.FindByIdAsync(request.SystemRoleId, ct);
        if (role is null || role.Scope != RoleScope.Platform) return Results.BadRequest(new { message = "角色不存在或不是平台範圍角色。" });

        var id = await users.CreateAsync(new AppUser
        {
            Username = request.Username.Trim(),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = PasswordHasher.Hash(request.Password),
            SystemRoleId = request.SystemRoleId,
        }, ct);

        await opLog.LogAsync(scope!, "platform.system_user.create", "app_user", id.ToString(),
            $"建立平台系統帳號「{request.DisplayName.Trim()}」（{request.Username.Trim()}），角色：{role.Name}",
            after: new { request.Username, request.DisplayName, RoleName = role.Name }, ct: ct);
        return Results.Created($"/api/v1/platform/system-users/{id}", new { id });
    }

    // ---- 角色 ----

    private static async Task<IResult> ListRoles(ClaimsPrincipal principal, RoleRepository repo, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.roles", "read", out _)) return Results.Forbid();
        return Results.Ok(await repo.ListAsync(ct: ct));
    }

    private static async Task<IResult> CreateRole(
        ClaimsPrincipal principal, CreateRoleRequest request, RoleRepository repo, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.roles", "create", out var scope)) return Results.Forbid();
        var id = await repo.CreateAsync(new AppRole
        {
            Code = request.Code.Trim(), Name = request.Name.Trim(), Scope = request.Scope, MerchantId = request.MerchantId,
        }, ct);

        await opLog.LogAsync(scope!, "platform.role.create", "app_role", id.ToString(),
            $"建立平台角色「{request.Name.Trim()}」（{request.Code.Trim()}）",
            after: new { request.Code, request.Name, request.Scope, request.MerchantId }, ct: ct);
        return Results.Created($"/api/v1/platform/roles/{id}", new { id });
    }

    private static async Task<IResult> GetRolePermissions(ClaimsPrincipal principal, int roleId, RoleRepository repo, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.roles", "read", out _)) return Results.Forbid();
        return Results.Ok(await repo.GetRolePermissionsAsync(roleId, ct));
    }

    /// <summary>
    /// 系統內建角色僅 platform-admin 可修改（比照美達特：IsSystem 角色鎖定，只有平台管理員能動）。
    /// </summary>
    private static async Task<IResult> SetRolePermissions(
        ClaimsPrincipal principal, int roleId, SetRolePermissionRequest[] requests, RoleRepository repo,
        OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.IsPlatform) return Results.Forbid();
        if (!scope.Has("platform.roles", "update")) return Results.Forbid();

        var role = await repo.FindByIdAsync(roleId, ct);
        if (role is null) return Results.NotFound();
        if (role.IsSystem && !scope.IsPlatformAdmin) return Results.Forbid();

        foreach (var request in requests)
        {
            await repo.SetRolePermissionAsync(roleId, request.PermissionCode,
                request.PerCreate, request.PerRead, request.PerUpdate, request.PerDelete, request.Options, ct);
        }

        await opLog.LogAsync(scope, "platform.role.permission.update", "app_role", roleId.ToString(),
            $"調整平台角色「{role.Name}」的權限設定（{requests.Length} 項資源）", after: requests, ct: ct);
        return Results.NoContent();
    }

    // ---- 權限目錄 ----

    private static async Task<IResult> ListPermissions(ClaimsPrincipal principal, PermissionRepository repo, CancellationToken ct)
    {
        if (!RequirePlatform(principal, "platform.permissions", "read", out _)) return Results.Forbid();
        return Results.Ok(await repo.ListAsync(ct: ct));
    }

    public sealed record CreateMerchantRequest(string Code, string Name, bool? IsRoleCrudConfigurationEnabled, bool? IsRoleOptionConfigurationEnabled);
    public sealed record UpdateMerchantRequest(bool? IsRoleCrudConfigurationEnabled, bool? IsRoleOptionConfigurationEnabled);
    public sealed record CreateMembershipRequest(int UserId, int RoleId);
    public sealed record CreateSystemUserRequest(string Username, string DisplayName, string Password, int SystemRoleId);
    public sealed record CreateRoleRequest(string Code, string Name, RoleScope Scope, int? MerchantId);
    public sealed record SetRolePermissionRequest(string PermissionCode, bool PerCreate, bool PerRead, bool PerUpdate, bool PerDelete, string[] Options);
}
