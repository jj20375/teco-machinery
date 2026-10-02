using System.Security.Claims;
using Microsoft.Extensions.Options;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Domain.Permissions;
using Teco.Hvac.Infrastructure.Permissions;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 登入與工作範圍切換（拿掉 tenant closure table 的部分——
/// TECO 場館是扁平的，一個 membership 就是一個場館 + 一個角色，不需要子節點繼承）。
/// </summary>
public static class AuthEndpoints
{
    private const int MaxFailuresBeforeLock = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/auth");

        group.MapPost("/login", Login);
        group.MapGet("/scopes", ListScopes).RequireAuthorization();
        group.MapPost("/scopes/select", SelectScope).RequireAuthorization();
        group.MapPost("/refresh", Refresh).RequireAuthorization();
        group.MapPost("/refresh-token", RefreshWithToken); // 刻意不掛 RequireAuthorization：access token 過期後才會用到這支
        group.MapGet("/me", GetMe).RequireAuthorization();
        group.MapPost("/change-password", ChangePassword).RequireAuthorization();
    }

    /// <summary>
    /// 使用者自行修改密碼。成功後遞增 AuthVersion，讓當下這顆 token 立即失效——
    /// 密碼異動後強制重新登入是預期行為，前端要接著清掉工作階段導去 /login。
    /// </summary>
    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest request, ClaimsPrincipal principal, UserRepository users, RefreshTokenRepository refreshTokens,
        OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null) return Results.Forbid();
        var user = await users.FindByIdAsync(scope.UserId, ct);
        if (user is null || !user.IsActive) return Results.Forbid();

        if (string.IsNullOrEmpty(request.CurrentPassword) || !PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            await opLog.LogAsync(scope, "auth.change_password", "app_user", user.Id.ToString(),
                "嘗試修改密碼但目前密碼輸入錯誤", isSuccess: false, errorMessage: "目前密碼不正確", ct: ct);
            return Results.Json(new { message = "目前密碼不正確。" }, statusCode: StatusCodes.Status400BadRequest);
        }
        // 前端驗證只是體驗優化，真正的規則邊界一定要在後端再檢查一次（規則見 PasswordPolicy）。
        if (PasswordPolicy.Validate(request.NewPassword) is { } passwordProblem)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [passwordProblem] });
        }

        await users.UpdatePasswordAsync(user.Id, PasswordHasher.Hash(request.NewPassword!), ct);
        await users.IncrementAuthVersionAsync(user.Id, ct);
        // 密碼改了，名下所有 refresh token 一起撤銷——不然舊 session 還能用舊密碼簽發前的
        // refresh token 換到新 access token，變相繞過「改密碼要重新登入」的預期行為。
        await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
        // 只記「自己改了密碼」這個事實，不記密碼本身——密碼永遠不進 operation_log。
        await opLog.LogAsync(scope, "auth.change_password", "app_user", user.Id.ToString(), "修改了自己的登入密碼", ct: ct);
        return Results.NoContent();
    }

    public sealed record ChangePasswordRequest(string CurrentPassword, string? NewPassword);

    private static async Task<IResult> Login(
        LoginRequest request, UserRepository users, MembershipRepository memberships, MerchantRepository merchants,
        PermissionGrantService grants, JwtTokenService jwt, RefreshTokenRepository refreshTokens,
        IOptions<JwtOptions> jwtOptions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["login"] = ["請輸入帳號與密碼。"] });
        }

        var user = await users.FindByUsernameAsync(request.Username.Trim(), ct);
        var now = DateTimeOffset.UtcNow;
        var isLocked = user?.LockedUntil is DateTimeOffset lockedUntil && lockedUntil > now;
        var passwordValid = user is not null && !isLocked && PasswordHasher.Verify(request.Password, user.PasswordHash);

        if (user is null || !user.IsActive || isLocked || !passwordValid)
        {
            if (user is not null && !isLocked) await users.RegisterLoginFailureAsync(user.Id, MaxFailuresBeforeLock, LockDuration, ct);
            // 不區分帳號不存在、密碼錯誤、帳號停用與鎖定，避免登入端點成為帳號枚舉來源。
            return Results.Json(new { message = "帳號或密碼不正確，或帳號已停用。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        await users.RegisterLoginSuccessAsync(user.Id, ct);
        var refreshToken = await IssueRefreshTokenAsync(refreshTokens, user, jwtOptions.Value, ct);

        if (user.SystemRoleId is not null || user.IsPlatformAdmin)
        {
            var platformGrants = await grants.ForPlatformUserAsync(user, ct);
            var token = jwt.Create(user, "platform", merchantId: null, platformGrants);
            return Results.Ok(BuildResponse(token, user, "platform", null, platformGrants, refreshToken: refreshToken));
        }

        var activeMemberships = await memberships.ListActiveForUserAsync(user.Id, ct);
        var membership = activeMemberships.FirstOrDefault();
        if (membership is null)
        {
            return Results.Json(new { message = "帳號未獲授權使用任何場館，請聯絡平台管理員。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var merchant = await merchants.FindByIdAsync(membership.MerchantId, ct);
        if (merchant is null || merchant.Status != "active")
        {
            return Results.Json(new { message = "所屬場館已停用，無法登入系統。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var merchantGrants = await grants.ForMerchantAsync(membership, merchant, ct);
        var merchantToken = jwt.Create(user, "merchant", merchant.Id, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled);
        return Results.Ok(BuildResponse(merchantToken, user, "merchant", merchant, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled, refreshToken));
    }

    /// <summary>簽發並落地一顆新的 refresh token，回傳明文（只有這一刻拿得到，資料庫只存 hash）。</summary>
    private static async Task<string> IssueRefreshTokenAsync(
        RefreshTokenRepository refreshTokens, AppUser user, JwtOptions options, CancellationToken ct)
    {
        var plainToken = RefreshTokenGenerator.GeneratePlainToken();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(options.RefreshTokenDays);
        await refreshTokens.CreateAsync(user.Id, RefreshTokenGenerator.Hash(plainToken), user.AuthVersion, expiresAt, ct);
        return plainToken;
    }

    /// <summary>
    /// access token 過期後用這支換發新的，不用重新輸入密碼。刻意不掛 RequireAuthorization——
    /// 呼叫這支的當下 access token 通常已經過期，要求帶有效 JWT 就自相矛盾了。
    /// 採 token 輪替：每次交換都撤銷舊 refresh token、發一顆新的，降低外洩後被重複使用的風險。
    /// </summary>
    private static async Task<IResult> RefreshWithToken(
        RefreshTokenRequest request, UserRepository users, MembershipRepository memberships, MerchantRepository merchants,
        PermissionGrantService grants, JwtTokenService jwt, RefreshTokenRepository refreshTokens,
        IOptions<JwtOptions> jwtOptions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)) return Results.Unauthorized();

        var stored = await refreshTokens.FindActiveAsync(RefreshTokenGenerator.Hash(request.RefreshToken), ct);
        if (stored is null) return Results.Unauthorized();

        var user = await users.FindByIdAsync(stored.UserId, ct);
        // AuthVersion 不符：密碼被改過或權限被調整過，這顆 refresh token 簽發當下的授權已經過期，
        // 即使還沒到 expires_at 也要拒絕，理由跟 access token 的 auth_version 檢查一致。
        if (user is null || !user.IsActive || user.AuthVersion != stored.AuthVersionAtIssue)
        {
            await refreshTokens.RevokeAsync(stored.Id, ct);
            return Results.Unauthorized();
        }

        await refreshTokens.RevokeAsync(stored.Id, ct);
        var newRefreshToken = await IssueRefreshTokenAsync(refreshTokens, user, jwtOptions.Value, ct);

        if (user.SystemRoleId is not null || user.IsPlatformAdmin)
        {
            var platformGrants = await grants.ForPlatformUserAsync(user, ct);
            var token = jwt.Create(user, "platform", merchantId: null, platformGrants);
            return Results.Ok(BuildResponse(token, user, "platform", null, platformGrants, refreshToken: newRefreshToken));
        }

        var activeMemberships = await memberships.ListActiveForUserAsync(user.Id, ct);
        var membership = activeMemberships.FirstOrDefault();
        if (membership is null) return Results.Unauthorized();

        var merchant = await merchants.FindByIdAsync(membership.MerchantId, ct);
        if (merchant is null || merchant.Status != "active") return Results.Unauthorized();

        var merchantGrants = await grants.ForMerchantAsync(membership, merchant, ct);
        var merchantToken = jwt.Create(user, "merchant", merchant.Id, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled);
        return Results.Ok(BuildResponse(merchantToken, user, "merchant", merchant, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled, newRefreshToken));
    }

    private static async Task<IResult> ListScopes(
        ClaimsPrincipal principal, UserRepository users, MembershipRepository memberships, MerchantRepository merchants, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null) return Results.Forbid();
        var user = await users.FindByIdAsync(scope.UserId, ct);
        if (user is null || !user.IsActive) return Results.Forbid();

        var options = new List<object>();
        if (user.SystemRoleId is not null || user.IsPlatformAdmin) options.Add(new { scopeKind = "platform", merchantId = (int?)null, name = "平台管理" });

        var activeMemberships = await memberships.ListActiveForUserAsync(user.Id, ct);
        foreach (var membership in activeMemberships)
        {
            var merchant = await merchants.FindByIdAsync(membership.MerchantId, ct);
            if (merchant is null || merchant.Status != "active") continue;
            options.Add(new { scopeKind = "merchant", merchantId = merchant.Id, name = merchant.Name });
        }
        return Results.Ok(options);
    }

    private static async Task<IResult> SelectScope(
        SelectScopeRequest request, ClaimsPrincipal principal, UserRepository users, MembershipRepository memberships,
        MerchantRepository merchants, PermissionGrantService grants, JwtTokenService jwt, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null) return Results.Forbid();
        var user = await users.FindByIdAsync(scope.UserId, ct);
        if (user is null || !user.IsActive) return Results.Forbid();

        if (string.Equals(request.ScopeKind, "platform", StringComparison.OrdinalIgnoreCase))
        {
            if (user.SystemRoleId is null && !user.IsPlatformAdmin) return Results.Forbid();
            var platformGrants = await grants.ForPlatformUserAsync(user, ct);
            var token = jwt.Create(user, "platform", merchantId: null, platformGrants);
            return Results.Ok(BuildResponse(token, user, "platform", null, platformGrants));
        }

        if (request.MerchantId is not int merchantId) return Results.BadRequest(new { message = "場館 scope 必須包含 merchantId。" });
        var membership = await memberships.FindAsync(merchantId, user.Id, ct);
        if (membership is null || !membership.IsActive) return Results.Forbid();

        var merchant = await merchants.FindByIdAsync(merchantId, ct);
        if (merchant is null || merchant.Status != "active") return Results.Forbid();

        var merchantGrants = await grants.ForMerchantAsync(membership, merchant, ct);
        var merchantToken = jwt.Create(user, "merchant", merchant.Id, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled);
        return Results.Ok(BuildResponse(merchantToken, user, "merchant", merchant, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled));
    }

    private static async Task<IResult> Refresh(
        ClaimsPrincipal principal, UserRepository users, MembershipRepository memberships, MerchantRepository merchants,
        PermissionGrantService grants, JwtTokenService jwt, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null) return Results.Forbid();
        var user = await users.FindByIdAsync(scope.UserId, ct);
        if (user is null || !user.IsActive) return Results.Forbid();

        if (scope.IsPlatform)
        {
            var platformGrants = await grants.ForPlatformUserAsync(user, ct);
            var token = jwt.Create(user, "platform", merchantId: null, platformGrants);
            return Results.Ok(BuildResponse(token, user, "platform", null, platformGrants));
        }

        if (scope.MerchantId is not int merchantId) return Results.Forbid();
        var membership = await memberships.FindAsync(merchantId, user.Id, ct);
        if (membership is null || !membership.IsActive) return Results.Forbid();

        var merchant = await merchants.FindByIdAsync(merchantId, ct);
        if (merchant is null || merchant.Status != "active") return Results.Forbid();

        var merchantGrants = await grants.ForMerchantAsync(membership, merchant, ct);
        var merchantToken = jwt.Create(user, "merchant", merchant.Id, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled);
        return Results.Ok(BuildResponse(merchantToken, user, "merchant", merchant, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled));
    }

    private static IResult GetMe(ClaimsPrincipal principal)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null) return Results.Forbid();
        return Results.Ok(new
        {
            userId = scope.UserId,
            scopeKind = scope.ScopeKind,
            merchantId = scope.MerchantId,
            isPlatformAdmin = scope.IsPlatformAdmin,
            permissions = scope.Permissions,
            options = scope.Options,
        });
    }

    private static object BuildResponse(
        string token, AppUser user, string scopeKind, Merchant? merchant, IReadOnlyCollection<PermissionGrant> grants,
        bool isRoleCrudConfigurationEnabled = true, bool isRoleOptionConfigurationEnabled = true, string? refreshToken = null) => new
    {
        accessToken = token,
        refreshToken,
        user = new
        {
            user.Id,
            user.Username,
            user.DisplayName,
            scopeKind,
            merchantId = merchant?.Id,
            merchantName = merchant?.Name,
            isPlatformAdmin = user.IsPlatformAdmin,
            isRoleCrudConfigurationEnabled,
            isRoleOptionConfigurationEnabled,
            grants,
            permissions = grants.Select(g => g.Code).ToArray(),
        },
    };

    public sealed record LoginRequest(string Username, string Password);
    public sealed record SelectScopeRequest(string ScopeKind, int? MerchantId);
    public sealed record RefreshTokenRequest(string RefreshToken);
}
