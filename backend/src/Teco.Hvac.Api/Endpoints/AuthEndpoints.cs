using System.Collections.Concurrent;
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
        group.MapPost("/logout", Logout).RequireAuthorization();
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

    /// <summary>
    /// 帳號不存在的失敗登入，同一個「帳號＋IP」一分鐘最多記一筆：存在的帳號最多連錯 5 次就鎖定、天然有上限，
    /// 但亂猜不存在的帳號沒有上限，不節流的話任何人都能用掃描流量把操作紀錄灌爆。
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTimeOffset> UnknownUserLogThrottle = new();

    private static bool ShouldLogUnknownUser(string username, string? ip)
    {
        var now = DateTimeOffset.UtcNow;
        if (UnknownUserLogThrottle.Count > 5000) UnknownUserLogThrottle.Clear();
        var key = $"{username.ToLowerInvariant()}|{ip}";
        if (UnknownUserLogThrottle.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1)) return false;
        UnknownUserLogThrottle[key] = now;
        return true;
    }

    /// <summary>登入失敗的稽核。原因只寫進操作紀錄給管理員看，回給前端的訊息仍然不區分（避免帳號枚舉）。</summary>
    private static async Task LogLoginFailureAsync(
        OperationLogger opLog, MembershipRepository memberships, HttpContext http, AppUser? user, string attemptedUsername,
        string reason, bool justLocked, CancellationToken ct)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString();
        if (user is null)
        {
            if (!ShouldLogUnknownUser(attemptedUsername, ip)) return;
            await opLog.LogAuthAsync(0, attemptedUsername, "（不存在的帳號）", null, OperationActionLabels.Login,
                "登入失敗：帳號不存在", isSuccess: false, errorMessage: reason, ct: ct);
            return;
        }
        // 場館成員的失敗登入掛在他的場館底下，場館管理員才看得到（平台帳號沒有場館，merchant_id 為空）
        int? merchantId = user.SystemRoleId is null && !user.IsPlatformAdmin
            ? (await memberships.ListActiveForUserAsync(user.Id, ct)).FirstOrDefault()?.MerchantId
            : null;
        await opLog.LogAuthAsync(user.Id, user.Username, user.DisplayName, merchantId, OperationActionLabels.Login,
            $"登入失敗：{reason}", isSuccess: false, errorMessage: reason, ct: ct);
        if (justLocked)
        {
            await opLog.LogAuthAsync(user.Id, user.Username, user.DisplayName, merchantId, OperationActionLabels.AccountLocked,
                $"連續登入失敗 {MaxFailuresBeforeLock} 次，帳號已鎖定 {(int)LockDuration.TotalMinutes} 分鐘", isSuccess: false,
                errorMessage: "連續登入失敗", ct: ct);
        }
    }

    private static async Task<IResult> Login(
        LoginRequest request, HttpContext http, UserRepository users, MembershipRepository memberships, MerchantRepository merchants,
        PermissionGrantService grants, JwtTokenService jwt, RefreshTokenRepository refreshTokens, OperationLogger opLog,
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
            var justLocked = user is not null && !isLocked
                && await users.RegisterLoginFailureAsync(user.Id, MaxFailuresBeforeLock, LockDuration, ct);
            var reason = user is null ? "帳號不存在" : !user.IsActive ? "帳號已停用" : isLocked ? "帳號鎖定中" : "密碼錯誤";
            await LogLoginFailureAsync(opLog, memberships, http, user, request.Username.Trim(), reason, justLocked, ct);
            // 不區分帳號不存在、密碼錯誤、帳號停用與鎖定，避免登入端點成為帳號枚舉來源。
            return Results.Json(new { message = "帳號或密碼不正確，或帳號已停用。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        await users.RegisterLoginSuccessAsync(user.Id, ct);
        var refreshToken = await IssueRefreshTokenAsync(refreshTokens, user, jwtOptions.Value, ct);

        if (user.SystemRoleId is not null || user.IsPlatformAdmin)
        {
            var platformGrants = await grants.ForPlatformUserAsync(user, ct);
            var token = jwt.Create(user, "platform", merchantId: null, platformGrants);
            await opLog.LogAuthAsync(user.Id, user.Username, user.DisplayName, null, OperationActionLabels.Login,
                "登入系統（平台管理）", isSuccess: true, ct: ct);
            return Results.Ok(BuildResponse(token, user, "platform", null, platformGrants, refreshToken: refreshToken));
        }

        var activeMemberships = await memberships.ListActiveForUserAsync(user.Id, ct);
        var membership = activeMemberships.FirstOrDefault();
        if (membership is null)
        {
            await opLog.LogAuthAsync(user.Id, user.Username, user.DisplayName, null, OperationActionLabels.Login,
                "登入失敗：帳號未獲授權使用任何場館", isSuccess: false, errorMessage: "沒有任何場館成員資格", ct: ct);
            return Results.Json(new { message = "帳號未獲授權使用任何場館，請聯絡平台管理員。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var merchant = await merchants.FindByIdAsync(membership.MerchantId, ct);
        if (merchant is null || merchant.Status != "active")
        {
            await opLog.LogAuthAsync(user.Id, user.Username, user.DisplayName, membership.MerchantId, OperationActionLabels.Login,
                "登入失敗：所屬場館已停用", isSuccess: false, errorMessage: "場館已停用", ct: ct);
            return Results.Json(new { message = "所屬場館已停用，無法登入系統。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var merchantGrants = await grants.ForMerchantAsync(membership, merchant, ct);
        var merchantToken = jwt.Create(user, "merchant", merchant.Id, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled);
        await opLog.LogAuthAsync(user.Id, user.Username, user.DisplayName, merchant.Id, OperationActionLabels.Login,
            $"登入系統（{merchant.Name}）", isSuccess: true, ct: ct);
        return Results.Ok(BuildResponse(merchantToken, user, "merchant", merchant, merchantGrants,
            merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled, refreshToken));
    }

    /// <summary>
    /// 登出：撤銷這個瀏覽器的 refresh token（只撤銷呼叫端帶來的那一顆，不影響同一個帳號在別處的登入）並寫紀錄。
    /// 前端登出是盡力而為——這支失敗不能擋住登出，工作階段照樣在本機清掉。
    /// </summary>
    private static async Task<IResult> Logout(
        LogoutRequest? request, ClaimsPrincipal principal, RefreshTokenRepository refreshTokens, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null) return Results.Unauthorized();
        if (!string.IsNullOrWhiteSpace(request?.RefreshToken))
        {
            var stored = await refreshTokens.FindActiveAsync(RefreshTokenGenerator.Hash(request.RefreshToken), ct);
            if (stored is not null && stored.UserId == scope.UserId) await refreshTokens.RevokeAsync(stored.Id, ct);
        }
        await opLog.LogAsync(scope, OperationActionLabels.Logout, "app_user", scope.UserId.ToString(), "登出系統", ct: ct);
        return Results.NoContent();
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
    public sealed record LogoutRequest(string? RefreshToken);
}
