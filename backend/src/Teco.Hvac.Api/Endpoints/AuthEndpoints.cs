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
    /// <summary>帳號連續登入失敗達此門檻即觸發暫時鎖定保護</summary>
    private const int MaxFailuresBeforeLock = 5;
    /// <summary>帳號觸發鎖定後的持續時間（15 分鐘）</summary>
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 註冊身分驗證與權杖交換相關端點路由。
    /// </summary>
    /// <param name="app">Web 應用程式實例</param>
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // 建立身分驗證端點群組 /api/v1/auth
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
    /// <param name="request">修改密碼請求內容，包含目前密碼與新密碼</param>
    /// <param name="principal">目前呼叫端的 ClaimsPrincipal 身分識別</param>
    /// <param name="users">使用者資料存取倉儲</param>
    /// <param name="refreshTokens">更新權杖倉儲，用於撤銷既有工作階段</param>
    /// <param name="opLog">操作稽核日誌記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功回傳 204 NoContent，驗證失敗回傳 400 BadRequest</returns>
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

    /// <summary>
    /// 修改密碼請求資料傳輸物件。
    /// </summary>
    /// <param name="CurrentPassword">使用者當前之明文密碼</param>
    /// <param name="NewPassword">欲變更之新明文密碼</param>
    public sealed record ChangePasswordRequest(string CurrentPassword, string? NewPassword);

    /// <summary>
    /// 帳號不存在的失敗登入，同一個「帳號＋IP」一分鐘最多記一筆：存在的帳號最多連錯 5 次就鎖定、天然有上限，
    /// 但亂猜不存在的帳號沒有上限，不節流的話任何人都能用掃描流量把操作紀錄灌爆。
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTimeOffset> UnknownUserLogThrottle = new();

    /// <summary>
    /// 判斷是否需要針對不存在的帳號寫入登入失敗稽核日誌（以 IP 與帳號維度進行每分鐘一次的節流）。
    /// </summary>
    /// <param name="username">嘗試登入之使用者名稱</param>
    /// <param name="ip">來源客戶端 IP 位址</param>
    /// <returns>若超過節流區間則回傳 true 允許記錄；否則回傳 false 略過</returns>
    private static bool ShouldLogUnknownUser(string username, string? ip)
    {
        var now = DateTimeOffset.UtcNow;
        if (UnknownUserLogThrottle.Count > 5000) UnknownUserLogThrottle.Clear();
        var key = $"{username.ToLowerInvariant()}|{ip}";
        if (UnknownUserLogThrottle.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1)) return false;
        UnknownUserLogThrottle[key] = now;
        return true;
    }

    /// <summary>
    /// 登入失敗的稽核。原因只寫進操作紀錄給管理員看，回給前端的訊息仍然不區分（避免帳號枚舉）。
    /// </summary>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="memberships">成員關係倉儲，用於查詢所屬場館 ID</param>
    /// <param name="http">當前 HTTP 請求上下文</param>
    /// <param name="user">查詢到的使用者實體（若帳號不存在則為 null）</param>
    /// <param name="attemptedUsername">嘗試登入的使用者帳號名稱</param>
    /// <param name="reason">失敗原因文字描述</param>
    /// <param name="justLocked">是否剛好達到連續失敗上限而觸發鎖定</param>
    /// <param name="ct">非同步作業取消權杖</param>
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

    /// <summary>
    /// 使用者登入端點處理函式。驗證帳密、鎖定狀態、取得所屬場館或平台角色權限，並簽發 Access Token 與 Refresh Token。
    /// </summary>
    /// <param name="request">登入請求物件，含帳號與密碼</param>
    /// <param name="http">當前 HTTP 請求上下文</param>
    /// <param name="users">使用者資料存取倉儲</param>
    /// <param name="memberships">成員與場館關聯倉儲</param>
    /// <param name="merchants">場館基本資料倉儲</param>
    /// <param name="grants">角色權限計算服務</param>
    /// <param name="jwt">JWT 簽發服務</param>
    /// <param name="refreshTokens">更新權杖管理倉儲</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="jwtOptions">JWT 選項設定，含過期天數</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：包含 Token、使用者資訊、權限清單與 Refresh Token</returns>
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
    /// <param name="request">登出請求資料，可選攜帶欲撤銷之 RefreshToken</param>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="refreshTokens">更新權杖倉儲</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功回傳 204 NoContent</returns>
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
    /// <param name="refreshTokens">更新權杖倉儲</param>
    /// <param name="user">目標使用者實體</param>
    /// <param name="options">JWT 設定選項，提供 RefreshToken 有效天數</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>簽發出的明文 Refresh Token 字串</returns>
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
    /// <param name="request">換發權杖請求物件，含明文 Refresh Token</param>
    /// <param name="users">使用者資料存取倉儲</param>
    /// <param name="memberships">成員與場館關聯倉儲</param>
    /// <param name="merchants">場館基本資料倉儲</param>
    /// <param name="grants">角色權限計算服務</param>
    /// <param name="jwt">JWT 簽發服務</param>
    /// <param name="refreshTokens">更新權杖管理倉儲</param>
    /// <param name="jwtOptions">JWT 選項設定</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功簽發新 Access Token 與輪替之 Refresh Token</returns>
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

    /// <summary>
    /// 列出當前登入使用者所有可切換的工作範圍清單（包含平台管理及各個場館身分）。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="users">使用者資料存取倉儲</param>
    /// <param name="memberships">成員與場館關聯倉儲</param>
    /// <param name="merchants">場館基本資料倉儲</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>可選的工作範圍清單陣列</returns>
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

    /// <summary>
    /// 切換當前使用者的工作範圍（例如切換至特定場館或切回平台管理），並簽發對應範圍的 JWT。
    /// </summary>
    /// <param name="request">選取範圍請求內容，包含 scopeKind 與 merchantId</param>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="users">使用者資料存取倉儲</param>
    /// <param name="memberships">成員與場館關聯倉儲</param>
    /// <param name="merchants">場館基本資料倉儲</param>
    /// <param name="grants">角色權限計算服務</param>
    /// <param name="jwt">JWT 簽發服務</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功回傳該範圍的新 Token 及使用者資訊</returns>
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

    /// <summary>
    /// 在已登入且權杖尚未過期的狀態下，依目前的使用範圍重新載入最新授權並更新 Access Token。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="users">使用者資料存取倉儲</param>
    /// <param name="memberships">成員與場館關聯倉儲</param>
    /// <param name="merchants">場館基本資料倉儲</param>
    /// <param name="grants">角色權限計算服務</param>
    /// <param name="jwt">JWT 簽發服務</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：更新後的 Token 與範圍資訊</returns>
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

    /// <summary>
    /// 取得目前登入者的身分與範圍摘要資訊（供前端初始化或檢查權限狀態）。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <returns>使用者 ID、身分類別、場館 ID、權限集合等物件</returns>
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

    /// <summary>
    /// 組裝統一的登入/切換範圍/更新授權回傳資料結構。
    /// </summary>
    /// <param name="token">簽發出之 Access Token 字串</param>
    /// <param name="user">使用者實體</param>
    /// <param name="scopeKind">範圍種類 ("platform" 或 "merchant")</param>
    /// <param name="merchant">場館實體（若是平台範圍則為 null）</param>
    /// <param name="grants">授予之權限清單</param>
    /// <param name="isRoleCrudConfigurationEnabled">是否啟用角色建立/修改功能</param>
    /// <param name="isRoleOptionConfigurationEnabled">是否啟用功能細項授權設定</param>
    /// <param name="refreshToken">可選之明文 Refresh Token 字串</param>
    /// <returns>匿名回傳物件</returns>
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

    /// <summary>登入要求資料傳輸物件</summary>
    /// <param name="Username">登入帳號</param>
    /// <param name="Password">登入密碼</param>
    public sealed record LoginRequest(string Username, string Password);

    /// <summary>選取工作範圍請求資料傳輸物件</summary>
    /// <param name="ScopeKind">工作範圍種類 ("platform" 或 "merchant")</param>
    /// <param name="MerchantId">場館 ID（若為場館範圍則必填）</param>
    public sealed record SelectScopeRequest(string ScopeKind, int? MerchantId);

    /// <summary>以 Refresh Token 換取 Access Token 請求資料傳輸物件</summary>
    /// <param name="RefreshToken">明文更新權杖</param>
    public sealed record RefreshTokenRequest(string RefreshToken);

    /// <summary>登出請求資料傳輸物件</summary>
    /// <param name="RefreshToken">欲一併撤銷之明文更新權杖（可選）</param>
    public sealed record LogoutRequest(string? RefreshToken);
}

