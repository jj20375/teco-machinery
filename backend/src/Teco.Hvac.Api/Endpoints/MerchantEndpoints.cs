using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Domain.Permissions;
using Teco.Hvac.Infrastructure.Permissions;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 場館自己的日常管理：自家帳號與自訂角色。只有 merchant scope 的 token 能呼叫，
/// 且必須持有 merchant.users／merchant.roles 權限——平台管理員要動場館資料得先切到該場館 scope。
/// 自訂角色的每一項 CRUD／子功能都不得超過 merchant-admin 的實際授予。
/// </summary>
public static class MerchantEndpoints
{
    /// <summary>
    /// 註冊場館管理員相關端點路由。
    /// </summary>
    /// <param name="app">Web 應用程式實例</param>
    public static void MapMerchantEndpoints(this WebApplication app)
    {
        // 場館後台路由群組 /api/v1/merchant，強制要求 JWT 驗證
        var group = app.MapGroup("/api/v1/merchant").RequireAuthorization();

        group.MapGet("/users", ListUsers);
        group.MapPost("/users", AddUser);
        group.MapPatch("/users/{membershipId:int}", UpdateUser);
        group.MapDelete("/users/{membershipId:int}", DeleteUser);
        group.MapPost("/users/{membershipId:int}/reset-password", ResetUserPassword);
        group.MapGet("/users/{membershipId:int}/features", GetUserFeatures);
        group.MapPut("/users/{membershipId:int}/features", SetUserFeatures);

        group.MapGet("/roles", ListRoles);
        group.MapGet("/roles/code-availability", CheckRoleCodeAvailability);
        group.MapPost("/roles", CreateRole);
        group.MapPatch("/roles/{roleId:int}", RenameRole);
        group.MapDelete("/roles/{roleId:int}", DeleteRole);
        group.MapGet("/roles/{roleId:int}/permissions", GetRolePermissions);
        group.MapPost("/roles/{roleId:int}/permissions", SetRolePermissions);
        group.MapGet("/permissions", ListPermissionCatalog);

        group.MapGet("/operation-logs", ListOperationLogs);
    }

    // action 代碼 → 畫面「動作類型」欄的分類標籤見 OperationActionLabels（找不到對照時顯示代碼本身，不擋畫面）。

    /// <summary>操作紀錄查詢之最大允許天數範圍限制（一年）</summary>
    private const int MaxOperationLogRangeDays = 366; // 對齊畫面「最大範圍：一年」的查詢限制。

    /// <summary>
    /// 場館操作紀錄查詢。from/to 未帶時區資訊時，ASP.NET Core 的 DateTime binder 會先解成
    /// Unspecified kind，.ToUniversalTime() 再依容器的 TZ=Asia/Taipei 換算成 UTC——
    /// 跟 ChillerEndpoints/FcuEndpoints 的 history 查詢是同一套慣例。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="from">查詢起始時間</param>
    /// <param name="to">查詢結束時間</param>
    /// <param name="repo">操作日誌倉儲實例</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：操作紀錄清單，含時間戳記、操作者、動作分類與內容</returns>
    private static async Task<IResult> ListOperationLogs(
        ClaimsPrincipal principal, DateTime from, DateTime to, OperationLogRepository repo, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.operation_log", "read", out var scope)) return Results.Forbid();

        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        if (toUtc < fromUtc)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["結束日期不可早於起始日期。"] });
        }
        if ((toUtc - fromUtc).TotalDays > MaxOperationLogRangeDays)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["to"] = [$"查詢區間最大範圍為 {MaxOperationLogRangeDays} 天，請縮小日期區間。"],
            });
        }

        var rows = await repo.ListForMerchantAsync(scope!.MerchantId!.Value, fromUtc, toUtc, limit: 500, ct);
        return Results.Ok(rows.Select(r => new
        {
            id = r.Id,
            timestamp = new DateTime(r.CreatedAtUtc.Ticks, DateTimeKind.Utc),
            username = r.ActorUsername,
            userFullName = r.ActorDisplayName,
            action = r.Action,
            actionType = OperationActionLabels.For(r.Action),
            content = r.Summary,
            ipAddress = r.Ip,
            isSuccess = r.IsSuccess,
        }));
    }

    /// <summary>
    /// 新增／編輯使用者畫面上的功能核取方塊，順序即畫面顯示順序。每一項對應一個權限資源；勾選＝給 read，
    /// 發 JWT 時 read 會自動展開成完整 CRUD ＋ 該資源全部子功能（場館的「CRUD 簡化模式」），所以畫面只需要開／關。
    /// 全部勾選就等於場館管理員（見 AssignFeaturesAsync）；場館角色管理（merchant.roles）不在清單內，只有管理員擁有。
    /// </summary>
    private static readonly (string Code, string Name)[] MemberFeatures =
    [
        ("hvac.overview", "監控中心"), ("hvac.floor_plan", "空間設備配置"), ("hvac.chillers", "冰水主機管理"),
        ("hvac.fcus", "FCU管理"), ("hvac.alarms", "告警"), ("hvac.thresholds", "告警門檻"), ("hvac.reports", "報表統計匯出"),
        ("merchant.users", "使用者管理"), ("merchant.operation_log", "操作紀錄"),
    ];
    /// <summary>所有功能代碼之快取字串陣列</summary>
    private static readonly string[] MemberFeatureCodes = MemberFeatures.Select(f => f.Code).ToArray();

    /// <summary>
    /// 驗證呼叫者是否為合法場館範圍，且具備指定資源與動作之存取權限。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="code">權限資源代碼（例如 merchant.users）</param>
    /// <param name="action">操作動作（例如 read, create, update, delete）</param>
    /// <param name="scope">解析成功之 RequestScope 物件</param>
    /// <returns>若符合場館範圍且具備權限則為 true；否則為 false</returns>
    private static bool RequireMerchant(ClaimsPrincipal principal, string code, string action, out RequestScope? scope)
    {
        scope = null;
        if (!RequestScope.TryRead(principal, out scope) || scope is null) return false;
        return !scope.IsPlatform && scope.MerchantId is not null && scope.Has(code, action);
    }

    /// <summary>
    /// 取得場館成員清單（包含帳號、名稱、角色、鎖定狀態與最後登入時間）。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="repo">成員關係倉儲實例</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成員清單陣列</returns>
    private static async Task<IResult> ListUsers(ClaimsPrincipal principal, MembershipRepository repo, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.users", "read", out var scope)) return Results.Forbid();
        var rows = await repo.ListForMerchantAsync(scope!.MerchantId!.Value, ct);
        var now = DateTimeOffset.UtcNow;
        return Results.Ok(rows.Select(r => new
        {
            membershipId = r.MembershipId,
            r.UserId,
            r.Username,
            r.DisplayName,
            r.Email,
            r.IsActive,
            r.IsOwner,
            r.RoleId,
            r.RoleCode,
            r.RoleName,
            r.LastLoginAt,
            isLocked = r.LockedUntil is DateTimeOffset lockedUntil && lockedUntil > now,
        }));
    }

    /// <summary>
    /// 新增場館帳號。若帳號已存在（跨場館共用同一登入主體）則只新增 membership，不建立新使用者；
    /// 這種情況下 request 的 DisplayName/Email/Password 會被忽略，沿用既有帳號資料。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="request">新增成員請求物件，包含帳號、密碼、功能清單或角色 ID</param>
    /// <param name="users">使用者倉儲</param>
    /// <param name="memberships">成員關係倉儲</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：建立之 membership ID 與資源路徑</returns>
    private static async Task<IResult> AddUser(
        ClaimsPrincipal principal, AddMerchantUserRequest request,
        UserRepository users, MembershipRepository memberships, RoleRepository roles, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.users", "create", out var scope)) return Results.Forbid();

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["帳號與使用者名稱為必填。"] });
        }

        // 新版畫面直接帶功能清單（Features）；舊的 RoleId 寫法保留給既有腳本與相容性。
        AppRole? role = null;
        var features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.Features is not null)
        {
            if (NormalizeFeatures(scope!, request.Features, out features) is { } featureProblem) return featureProblem;
        }
        else
        {
            if (request.RoleId is not int requestedRoleId) return Results.BadRequest(new { message = "請選擇這個使用者可以使用的功能。" });
            role = await roles.FindByIdAsync(requestedRoleId, ct);
            if (role is null || role.Scope != RoleScope.Merchant) return Results.BadRequest(new { message = "角色不存在或不是場館範圍角色。" });
        }

        var existingUser = await users.FindByUsernameAsync(request.Username.Trim(), ct);
        if (existingUser is not null)
        {
            var existingMembership = await memberships.FindAsync(scope!.MerchantId!.Value, existingUser.Id, ct);
            if (existingMembership is not null) return Results.Conflict(new { message = "此帳號已經是本場館的成員。" });
        }

        if (existingUser is null)
        {
            // 掛既有帳號時密碼會被忽略，只有新建帳號才檢查
            if (string.IsNullOrWhiteSpace(request.Password))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["新建帳號必須提供初始密碼。"] });
            if (PasswordPolicy.Validate(request.Password) is { } passwordProblem)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [passwordProblem] });
        }

        var userId = existingUser?.Id ?? await users.CreateAsync(new AppUser
        {
            Username = request.Username.Trim(),
            DisplayName = request.DisplayName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            PasswordHash = PasswordHasher.Hash(request.Password!),
        }, ct);

        var id = await memberships.CreateAsync(
            new MerchantMembership { MerchantId = scope!.MerchantId!.Value, UserId = userId, RoleId = role?.Id }, ct);
        if (role is null) await AssignFeaturesAsync(id, features, scope, memberships, roles, ct);

        var permissionSummary = role is not null ? $"角色：{role.Name}" : DescribeFeatures(features);
        await opLog.LogAsync(scope, "merchant.user.create", "merchant_membership", id.ToString(),
            $"新增使用者「{request.DisplayName.Trim()}」（{request.Username.Trim()}），{permissionSummary}",
            after: new { request.Username, request.DisplayName, request.Email, RoleId = role?.Id, RoleName = role?.Name, Features = role is null ? features.ToArray() : null }, ct: ct);
        return Results.Created($"/api/v1/merchant/users/{id}", new { id });
    }

    /// <summary>被層級保護或業務規則擋下的成員管理操作，一樣要留下紀錄（is_success=false）。</summary>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="users">使用者倉儲</param>
    /// <param name="scope">當前呼叫端權限範圍</param>
    /// <param name="action">日誌動作代碼</param>
    /// <param name="target">目標成員關係實體</param>
    /// <param name="verb">動作動詞說明（例如 "刪除"、"重設密碼"）</param>
    /// <param name="reason">拒絕原因說明文字</param>
    /// <param name="ct">非同步作業取消權杖</param>
    private static async Task LogMemberDeniedAsync(
        OperationLogger opLog, UserRepository users, RequestScope scope, string action, MerchantMembership target,
        string verb, string reason, CancellationToken ct)
    {
        var targetUser = await users.FindByIdAsync(target.UserId, ct);
        await opLog.LogDeniedAsync(scope, action, "merchant_membership", target.Id.ToString(),
            $"嘗試{verb}「{targetUser?.DisplayName}」（{targetUser?.Username}）但被拒絕：{reason}", reason, ct);
    }

    /// <summary>
    /// 更新場館成員資訊（角色、啟用狀態或顯示姓名）。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="membershipId">欲更新之成員關係 ID</param>
    /// <param name="request">更新成員請求物件，包含欲調整的角色 ID、啟用狀態或顯示姓名</param>
    /// <param name="memberships">成員關係倉儲</param>
    /// <param name="users">使用者倉儲</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功回傳 204 NoContent</returns>
    private static async Task<IResult> UpdateUser(
        ClaimsPrincipal principal, int membershipId, UpdateMerchantUserRequest request,
        MembershipRepository memberships, UserRepository users, RoleRepository roles, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null || scope.IsPlatform || scope.MerchantId is null)
            return Results.Forbid();
        if (!scope.Has("merchant.users", "update")) return Results.Forbid();
        if (request.RoleId is not null && !scope.HasOption("merchant.users", "assign_role")) return Results.Forbid();

        string? trimmedDisplayName = null;
        if (request.DisplayName is not null)
        {
            trimmedDisplayName = request.DisplayName.Trim();
            if (trimmedDisplayName.Length == 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["displayName"] = ["使用者名稱不能是空白。"] });
        }

        // 一定要先查出這個 membership 屬於哪個場館，否則管理員理論上能對別的場館的 membershipId
        // 呼叫這支 API（PATCH body 沒有 merchantId 可核對，membershipId 是全域唯一的整數）。
        var membership = await memberships.FindByIdAsync(membershipId, ct);
        if (membership is null || membership.MerchantId != scope.MerchantId) return Results.NotFound();

        // 管理員／擁有者的層級保護（見 MerchantMembershipGuard）：只擋「真的有變動」的欄位，
        // 前端送一樣的值（例如存檔時帶著原本的角色）不算。
        var currentName = (await users.FindByIdAsync(membership.UserId, ct))?.DisplayName;
        var attempts = new List<MemberAction>();
        if (request.RoleId is not null && request.RoleId != membership.RoleId) attempts.Add(MemberAction.ChangeRole);
        if (request.IsActive is not null && request.IsActive != membership.IsActive)
            attempts.Add(request.IsActive.Value ? MemberAction.Activate : MemberAction.Deactivate);
        if (trimmedDisplayName is not null && trimmedDisplayName != currentName) attempts.Add(MemberAction.Rename);
        foreach (var attempt in attempts)
        {
            if (await MerchantMembershipGuard.CheckAsync(scope, membership, attempt, memberships, roles, ct) is { } denied)
            {
                await LogMemberDeniedAsync(opLog, users, scope, "merchant.user.update", membership, MerchantMembershipGuard.Verb(attempt), denied, ct);
                return MerchantMembershipGuard.Denied(denied);
            }
        }

        await memberships.UpdateRoleAsync(membershipId, request.RoleId, request.IsActive, ct);
        // 只讓「被改動的那個人」的舊 token 失效，不要整個場館一起踢——否則管理員每改一次
        // 別人的角色，自己也會被登出（實測發現的體驗問題，不是資安需要）。
        // 而且只有角色或啟用狀態「真的有變」才需要：這兩個會影響權限，舊 token 要作廢；
        // 只改顯示名稱不影響任何權限，不該讓人被登出（改自己的名字會被踢出去是使用者回報的問題）。
        if (attempts.Contains(MemberAction.ChangeRole) || attempts.Contains(MemberAction.Activate) || attempts.Contains(MemberAction.Deactivate))
            await users.IncrementAuthVersionAsync(membership.UserId, ct);

        var targetUser = await users.FindByIdAsync(membership.UserId, ct);
        var targetName = targetUser?.DisplayName ?? $"membership#{membershipId}";
        var summaryParts = new List<string>();
        if (request.RoleId is not null && request.RoleId != membership.RoleId)
        {
            var newRole = await roles.FindByIdAsync(request.RoleId.Value, ct);
            summaryParts.Add($"角色改為「{newRole?.Name ?? request.RoleId.ToString()}」");
        }
        if (request.IsActive is not null && request.IsActive != membership.IsActive)
        {
            summaryParts.Add(request.IsActive.Value ? "啟用帳號" : "停用帳號");
        }
        if (trimmedDisplayName is not null && trimmedDisplayName != targetName)
        {
            await users.UpdateDisplayNameAsync(membership.UserId, trimmedDisplayName, ct);
            summaryParts.Add($"姓名改為「{trimmedDisplayName}」");
        }
        if (summaryParts.Count > 0)
        {
            await opLog.LogAsync(scope, "merchant.user.update", "merchant_membership", membershipId.ToString(),
                $"將「{targetName}」{string.Join('、', summaryParts)}",
                before: new { membership.RoleId, membership.IsActive, DisplayName = targetName },
                after: new { RoleId = request.RoleId ?? membership.RoleId, IsActive = request.IsActive ?? membership.IsActive, DisplayName = trimmedDisplayName ?? targetName }, ct: ct);
        }
        return Results.NoContent();
    }

    /// <summary>
    /// 刪掉的是這個人在本場館的成員資格（membership），不是 app_user 這個帳號本身——
    /// 見 MembershipRepository.DeleteAsync 上的說明。兩個安全擋板：不能刪自己（避免手滑
    /// 把自己踢出場館、變成沒人能管理）；不能刪掉最後一個在職的場館管理員（不然整個場館的
    /// 使用者/角色管理會變成沒有人能維護，等於場館被鎖死）。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="membershipId">欲刪除之成員關係 ID</param>
    /// <param name="memberships">成員關係倉儲</param>
    /// <param name="users">使用者倉儲</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="refreshTokens">更新權杖倉儲，用於作廢該使用者之工作階段</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功回傳 204 NoContent</returns>
    private static async Task<IResult> DeleteUser(
        ClaimsPrincipal principal, int membershipId, MembershipRepository memberships, UserRepository users,
        RoleRepository roles, RefreshTokenRepository refreshTokens, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null || scope.IsPlatform || scope.MerchantId is null)
            return Results.Forbid();
        if (!scope.Has("merchant.users", "delete")) return Results.Forbid();

        var membership = await memberships.FindByIdAsync(membershipId, ct);
        if (membership is null || membership.MerchantId != scope.MerchantId) return Results.NotFound();

        if (membership.UserId == scope.UserId)
        {
            await LogMemberDeniedAsync(opLog, users, scope, "merchant.user.delete", membership, "刪除", "無法刪除自己的帳號。", ct);
            return Results.Json(new { message = "無法刪除自己的帳號。" }, statusCode: StatusCodes.Status400BadRequest);
        }
        if (await MerchantMembershipGuard.CheckAsync(scope, membership, MemberAction.Delete, memberships, roles, ct) is { } denied)
        {
            await LogMemberDeniedAsync(opLog, users, scope, "merchant.user.delete", membership, "刪除", denied, ct);
            return MerchantMembershipGuard.Denied(denied);
        }

        var targetUser = await users.FindByIdAsync(membership.UserId, ct);
        var targetName = targetUser?.DisplayName ?? $"membership#{membershipId}";

        var merchantUsers = await memberships.ListForMerchantAsync(scope.MerchantId.Value, ct);
        var activeAdminCount = merchantUsers.Count(u => u.RoleCode == "merchant-admin" && u.IsActive);
        var targetIsActiveAdmin = merchantUsers.Any(u => u.MembershipId == membershipId && u.RoleCode == "merchant-admin" && u.IsActive);
        if (targetIsActiveAdmin && activeAdminCount <= 1)
        {
            await LogMemberDeniedAsync(opLog, users, scope, "merchant.user.delete", membership, "刪除", "這是目前唯一在職的場館管理員。", ct);
            return Results.Json(
                new { message = "這是目前唯一在職的場館管理員，無法刪除，請先指派另一位場館管理員。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        await memberships.DeleteAsync(membershipId, ct);
        // 刪除後立刻讓這個人手上任何還沒過期的 access token／refresh token 一起失效，
        // 不然帳號雖然被移出場館，舊 token 理論上還能繼續用到自然過期。
        await refreshTokens.RevokeAllForUserAsync(membership.UserId, ct);
        await users.IncrementAuthVersionAsync(membership.UserId, ct);

        await opLog.LogAsync(scope, "merchant.user.delete", "merchant_membership", membershipId.ToString(),
            $"刪除了使用者「{targetName}」（{targetUser?.Username}）在本場館的帳號",
            before: new { targetUser?.Username, DisplayName = targetName, membership.RoleId }, ct: ct);
        return Results.NoContent();
    }

    /// <summary>
    /// 管理員幫成員重設密碼：產生一組隨機的臨時密碼，回傳給呼叫端一次（前端要立刻顯示、
    /// 明確告知管理員這是唯一能看到明碼的機會），同時解除鎖定並讓該帳號的舊 token 失效。
    /// 用隨機密碼而不是像舊版 mock 那樣用固定預設密碼（例如 12345@ABC）——固定密碼對所有
    /// 帳號都一樣，等於任何人都能用同一組密碼硬闖任何被重設過的帳號，是真的資安風險。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="membershipId">欲重設密碼之成員關係 ID</param>
    /// <param name="memberships">成員關係倉儲</param>
    /// <param name="users">使用者倉儲</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：包含一次性臨時明文密碼物件</returns>
    private static async Task<IResult> ResetUserPassword(
        ClaimsPrincipal principal, int membershipId, MembershipRepository memberships, UserRepository users,
        RoleRepository roles, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null || scope.IsPlatform || scope.MerchantId is null)
            return Results.Forbid();
        if (!scope.Has("merchant.users", "update") || !scope.HasOption("merchant.users", "reset_password")) return Results.Forbid();

        var membership = await memberships.FindByIdAsync(membershipId, ct);
        if (membership is null || membership.MerchantId != scope.MerchantId) return Results.NotFound();
        if (await MerchantMembershipGuard.CheckAsync(scope, membership, MemberAction.ResetPassword, memberships, roles, ct) is { } denied)
        {
            await LogMemberDeniedAsync(opLog, users, scope, "merchant.user.reset_password", membership, "重設密碼", denied, ct);
            return MerchantMembershipGuard.Denied(denied);
        }

        var temporaryPassword = TemporaryPasswordGenerator.Generate();
        await users.UpdatePasswordAsync(membership.UserId, PasswordHasher.Hash(temporaryPassword), ct);
        await users.ClearLockoutAsync(membership.UserId, ct);
        await users.IncrementAuthVersionAsync(membership.UserId, ct);

        var targetUser = await users.FindByIdAsync(membership.UserId, ct);
        // 只記「已重設」這個事實，不記密碼本身——密碼永遠不進 operation_log。
        await opLog.LogAsync(scope, "merchant.user.reset_password", "merchant_membership", membershipId.ToString(),
            $"重設了「{targetUser?.DisplayName ?? $"membership#{membershipId}"}」的登入密碼", ct: ct);
        return Results.Ok(new { temporaryPassword });
    }

    /// <summary>
    /// 編輯使用者畫面要預先勾選的功能：讀這個人目前指派角色的實際授予（系統範本、自訂角色、個人專屬角色都適用）。
    /// 全權限角色（場館管理員）沒有逐項資料列、是發 JWT 時動態算的，所以直接視為全部勾選。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="membershipId">目標成員關係 ID</param>
    /// <param name="memberships">成員關係倉儲</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：該成員已啟用的功能代碼清單</returns>
    private static async Task<IResult> GetUserFeatures(
        ClaimsPrincipal principal, int membershipId, MembershipRepository memberships, RoleRepository roles, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.users", "read", out var scope)) return Results.Forbid();
        var membership = await memberships.FindByIdAsync(membershipId, ct);
        if (membership is null || membership.MerchantId != scope!.MerchantId) return Results.NotFound();

        return Results.Ok(new { features = (await CurrentFeaturesAsync(membership, roles, ct)).ToArray() });
    }

    /// <summary>
    /// 取得指定成員關係目前所擁有的功能代碼清單。
    /// </summary>
    /// <param name="membership">成員關係實體</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>功能代碼唯讀清單</returns>
    private static async Task<IReadOnlyList<string>> CurrentFeaturesAsync(MerchantMembership membership, RoleRepository roles, CancellationToken ct)
    {
        if (membership.RoleId is not int roleId) return [];
        var role = await roles.FindByIdAsync(roleId, ct);
        if (role?.IsFullAccess == true) return MemberFeatureCodes;
        var enabled = new HashSet<string>(
            (await roles.GetRolePermissionsAsync(roleId, ct)).Where(d => d.PerRead).Select(d => d.PermissionCode), StringComparer.OrdinalIgnoreCase);
        return MemberFeatureCodes.Where(enabled.Contains).ToArray();
    }

    /// <summary>判斷指定角色是否為系統內建之場館管理員角色</summary>
    /// <param name="role">角色實體</param>
    /// <returns>若為內建管理員角色則回傳 true；否則為 false</returns>
    private static bool IsAdminRole(AppRole? role) => role is { IsSystem: true, Code: MerchantMembershipGuard.AdminRoleCode };

    /// <summary>
    /// 檢查並整理請求的功能清單。授予的每一項都必須是「操作者自己也有」的權限——不然只有「使用者管理」權限的人
    /// 把全部功能勾給自己，就等於升級成場館管理員。回傳 null 表示通過。
    /// </summary>
    /// <param name="scope">當前呼叫端權限範圍</param>
    /// <param name="raw">原始功能代碼陣列</param>
    /// <param name="features">標準化後的功能代碼雜湊集輸出</param>
    /// <returns>若驗證失敗回傳對應之 IResult（BadRequest 或 Denied）；若驗證通過則回傳 null</returns>
    private static IResult? NormalizeFeatures(RequestScope scope, string[]? raw, out HashSet<string> features)
    {
        features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in raw ?? [])
        {
            var match = MemberFeatures.FirstOrDefault(f => f.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (match.Code is null) return Results.BadRequest(new { message = $"未知的功能代碼：{code}" });
            features.Add(match.Code);
        }
        foreach (var (code, name) in MemberFeatures)
        {
            if (features.Contains(code) && !scope.Has(code, "read"))
                return MerchantMembershipGuard.Denied($"您本身沒有「{name}」的權限，無法授予給其他人。");
        }
        return null;
    }

    /// <summary>產出功能清單之繁體中文描述文字摘要（用於日誌與稽核）</summary>
    /// <param name="features">功能代碼集合</param>
    /// <returns>描述字串</returns>
    private static string DescribeFeatures(IReadOnlyCollection<string> features)
    {
        if (features.Count == MemberFeatures.Length) return "權限：場館管理員（全部功能）";
        if (features.Count == 0) return "權限：無任何功能";
        return "權限：" + string.Join('、', MemberFeatures.Where(f => features.Contains(f.Code)).Select(f => f.Name));
    }

    /// <summary>
    /// 把功能清單落實到這個成員身上：全部勾選就指到「場館管理員」系統角色（全權限、之後新增的功能也自動涵蓋、
    /// 管理員的各項保護機制照常運作）；否則給他一個專屬角色（第一次建立，之後沿用）並把清單寫進去。
    /// 絕不修改 merchant-admin／editor／viewer 這三個跨場館共用的系統範本。
    /// </summary>
    /// <param name="membershipId">成員關係 ID</param>
    /// <param name="features">欲指派之功能代碼集合</param>
    /// <param name="scope">當前工作範圍</param>
    /// <param name="memberships">成員關係倉儲</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="ct">非同步作業取消權杖</param>
    private static async Task AssignFeaturesAsync(
        int membershipId, HashSet<string> features, RequestScope scope, MembershipRepository memberships, RoleRepository roles, CancellationToken ct)
    {
        if (features.Count == MemberFeatures.Length)
        {
            var admin = await roles.FindByCodeAsync(RoleScope.Merchant, MerchantMembershipGuard.AdminRoleCode, null, ct)
                ?? throw new InvalidOperationException("找不到「場館管理員」系統角色。");
            await memberships.UpdateRoleAsync(membershipId, admin.Id, null, ct);
            return;
        }

        var personalRoleCode = $"member-{membershipId}";
        var personalRole = await roles.FindByCodeAsync(RoleScope.Merchant, personalRoleCode, scope.MerchantId, ct);
        var personalRoleId = personalRole?.Id ?? await roles.CreateAsync(new AppRole
        {
            Code = personalRoleCode, Name = "自訂權限", Scope = RoleScope.Merchant, MerchantId = scope.MerchantId, IsSystem = false,
        }, ct);

        foreach (var (code, _) in MemberFeatures)
            await roles.SetRolePermissionAsync(personalRoleId, code, false, features.Contains(code), false, false, [], ct);
        await memberships.UpdateRoleAsync(membershipId, personalRoleId, null, ct);
    }

    /// <summary>
    /// 儲存使用者的功能清單。全部勾選＝場館管理員，所以這裡等同「升級／降級管理員」：
    /// 沿用 MerchantMembershipGuard（擁有者不能被降級、其他管理員只有擁有者能動），並擋住「降級最後一位在職管理員」。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="membershipId">目標成員關係 ID</param>
    /// <param name="request">設定功能請求物件，含功能代碼陣列</param>
    /// <param name="memberships">成員關係倉儲</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="users">使用者倉儲</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功回傳 204 NoContent</returns>
    private static async Task<IResult> SetUserFeatures(
        ClaimsPrincipal principal, int membershipId, SetUserFeaturesRequest request,
        MembershipRepository memberships, RoleRepository roles, UserRepository users, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.users", "update", out var scope)) return Results.Forbid();
        if (!scope!.HasOption("merchant.users", "assign_role")) return Results.Forbid();
        var membership = await memberships.FindByIdAsync(membershipId, ct);
        if (membership is null || membership.MerchantId != scope.MerchantId) return Results.NotFound();

        if (NormalizeFeatures(scope, request.Features, out var features) is { } featureProblem) return featureProblem;

        // 勾選結果跟目前完全一樣就什麼都不做：不寫資料、不留紀錄，也不能讓對方的登入失效（自己存一次就被登出）。
        var beforeFeatures = await CurrentFeaturesAsync(membership, roles, ct);
        if (beforeFeatures.Count == features.Count && beforeFeatures.All(features.Contains)) return Results.NoContent();

        var currentRole = membership.RoleId is int currentRoleId ? await roles.FindByIdAsync(currentRoleId, ct) : null;
        var currentIsAdmin = IsAdminRole(currentRole);
        var newIsAdmin = features.Count == MemberFeatures.Length;
        if (currentIsAdmin != newIsAdmin)
        {
            var verb = newIsAdmin ? "升級為場館管理員" : "調降場館管理員的權限";
            if (await MerchantMembershipGuard.CheckAsync(scope, membership, MemberAction.ChangeRole, memberships, roles, ct) is { } denied)
            {
                await LogMemberDeniedAsync(opLog, users, scope, "merchant.user.features.update", membership, verb, denied, ct);
                return MerchantMembershipGuard.Denied(denied);
            }
            if (currentIsAdmin)
            {
                var merchantUsers = await memberships.ListForMerchantAsync(scope.MerchantId!.Value, ct);
                var activeAdminCount = merchantUsers.Count(u => u.RoleCode == MerchantMembershipGuard.AdminRoleCode && u.IsActive);
                if (membership.IsActive && activeAdminCount <= 1)
                {
                    const string reason = "這是目前唯一在職的場館管理員，無法調降權限，請先讓另一位使用者成為管理員（勾選全部功能）。";
                    await LogMemberDeniedAsync(opLog, users, scope, "merchant.user.features.update", membership, verb, reason, ct);
                    return Results.BadRequest(new { message = reason });
                }
            }
        }

        await AssignFeaturesAsync(membershipId, features, scope, memberships, roles, ct);
        // 只讓被改動的這個人的舊 token 失效（不整個場館一起踢，否則管理員每改一次別人權限自己也被登出）。
        await users.IncrementAuthVersionAsync(membership.UserId, ct);

        var targetUser = await users.FindByIdAsync(membership.UserId, ct);
        await opLog.LogAsync(scope, "merchant.user.features.update", "merchant_membership", membershipId.ToString(),
            $"調整了「{targetUser?.DisplayName ?? $"membership#{membershipId}"}」可使用的功能（{DescribeFeatures(features)}）",
            before: new { Features = beforeFeatures }, after: new { Features = MemberFeatureCodes.Where(features.Contains).ToArray() }, ct: ct);
        return Results.NoContent();
    }

    /// <summary>設定使用者功能代碼清單之請求物件</summary>
    /// <param name="Features">啟用的功能代碼陣列</param>
    public sealed record SetUserFeaturesRequest(string[]? Features);

    /// <summary>單一角色目前的細項授予——「編輯成員」面板用來預先勾選核取方塊。</summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="roleId">目標角色 ID</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：角色細項權限集合</returns>
    private static async Task<IResult> GetRolePermissions(
        ClaimsPrincipal principal, int roleId, RoleRepository roles, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "read", out _)) return Results.Forbid();
        return Results.Ok(await roles.GetRolePermissionsAsync(roleId, ct));
    }

    /// <summary>取得場館所有角色清單（包含系統範本與場館自訂角色）</summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="repo">角色倉儲</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：角色清單陣列</returns>
    private static async Task<IResult> ListRoles(ClaimsPrincipal principal, RoleRepository repo, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "read", out var scope)) return Results.Forbid();
        return Results.Ok(await repo.ListAsync(RoleScope.Merchant, scope!.MerchantId, ct));
    }

    /// <summary>
    /// 回傳 null 表示這個代碼可以用；否則是要直接顯示給使用者的原因。`member-` 開頭是「編輯成員」面板
    /// 自動建立的個人專屬角色用的（見 SetUserFeatures），而且不會出現在角色清單裡，使用者看不到、
    /// 也就無從得知已經被占用，所以一律保留。
    /// </summary>
    /// <param name="code">欲檢查之角色代碼</param>
    /// <param name="merchantId">當前場館 ID</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>若代碼可用回傳 null；否則回傳錯誤提示文字</returns>
    private static async Task<string?> ValidateNewRoleCodeAsync(string code, int? merchantId, RoleRepository roles, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return "角色代碼為必填。";
        if (code.StartsWith("member-", StringComparison.OrdinalIgnoreCase)) return "「member-」開頭是系統保留用途，請換一個代碼。";
        if (await roles.IsMerchantRoleCodeTakenAsync(code, merchantId, ct)) return $"角色代碼「{code}」已經存在，請換一個。";
        return null;
    }

    /// <summary>新增角色表單輸入代碼時即時檢查用；真正的防線仍在 CreateRole（送出時會再查一次）。</summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="code">待檢查的角色代碼</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：包含 available 布林值與提示訊息</returns>
    private static async Task<IResult> CheckRoleCodeAvailability(
        ClaimsPrincipal principal, string? code, RoleRepository roles, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "create", out var scope)) return Results.Forbid();
        var message = await ValidateNewRoleCodeAsync((code ?? string.Empty).Trim(), scope!.MerchantId, roles, ct);
        return Results.Ok(new { available = message is null, message });
    }

    /// <summary>建立新的場館自訂角色</summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="request">建立角色請求物件，含代碼與名稱</param>
    /// <param name="repo">角色倉儲</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：建立之角色 ID 與路徑</returns>
    private static async Task<IResult> CreateRole(
        ClaimsPrincipal principal, CreateMerchantRoleRequest request, RoleRepository repo, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "create", out var scope)) return Results.Forbid();

        var code = (request.Code ?? string.Empty).Trim();
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { message = "角色名稱為必填。" });
        var codeProblem = await ValidateNewRoleCodeAsync(code, scope!.MerchantId, repo, ct);
        if (codeProblem is not null) return Results.Conflict(new { message = codeProblem });

        var id = await repo.CreateAsync(new AppRole
        {
            Code = code, Name = name, Scope = RoleScope.Merchant, MerchantId = scope.MerchantId,
        }, ct);

        await opLog.LogAsync(scope, "merchant.role.create", "app_role", id.ToString(),
            $"建立場館自訂角色「{name}」（{code}）",
            after: new { Code = code, Name = name }, ct: ct);
        return Results.Created($"/api/v1/merchant/roles/{id}", new { id });
    }

    /// <summary>角色改名——只改顯示名稱，Code 建立後不能改（可能已經被其他地方引用比對）。</summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="roleId">欲更名之角色 ID</param>
    /// <param name="request">更名請求物件，含新角色名稱</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功回傳 204 NoContent</returns>
    private static async Task<IResult> RenameRole(
        ClaimsPrincipal principal, int roleId, RenameMerchantRoleRequest request, RoleRepository roles, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "update", out var scope)) return Results.Forbid();

        var role = await roles.FindByIdAsync(roleId, ct);
        if (role is null || role.Scope != RoleScope.Merchant || role.MerchantId != scope!.MerchantId) return Results.NotFound();
        if (role.IsSystem) return Results.Forbid(); // merchant-admin/editor/viewer 這三個系統範本僅平台管理員可改

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["角色名稱不能是空白。"] });
        }

        var newName = request.Name.Trim();
        await roles.RenameAsync(roleId, newName, ct);
        await opLog.LogAsync(scope, "merchant.role.rename", "app_role", roleId.ToString(),
            $"將角色「{role.Name}」改名為「{newName}」", before: new { role.Name }, after: new { Name = newName }, ct: ct);
        return Results.NoContent();
    }

    /// <summary>
    /// 刪除場館自訂角色。兩個擋板：系統範本角色（is_system）本來就刪不掉（Repository 的
    /// SQL 本身就有 WHERE is_system = 0），這裡先擋一次給清楚的錯誤訊息，不要讓它靜靜失敗；
    /// 還有成員在用這個角色時也不給刪，不然這些人會突然失去所有頁面存取權限，要先幫他們
    /// 換角色或停用帳號。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="roleId">欲刪除之角色 ID</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="memberships">成員關係倉儲，用於檢查是否有成員綁定該角色</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功回傳 204 NoContent</returns>
    private static async Task<IResult> DeleteRole(
        ClaimsPrincipal principal, int roleId, RoleRepository roles, MembershipRepository memberships, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "delete", out var scope)) return Results.Forbid();

        var role = await roles.FindByIdAsync(roleId, ct);
        if (role is null || role.Scope != RoleScope.Merchant || role.MerchantId != scope!.MerchantId) return Results.NotFound();
        if (role.IsSystem)
        {
            return Results.Json(new { message = "系統範本角色（場館管理員／編輯者／檢視者）無法刪除。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var memberCount = await memberships.CountByRoleAsync(roleId, ct);
        if (memberCount > 0)
        {
            return Results.Json(
                new { message = $"還有 {memberCount} 位成員使用這個角色，請先幫他們改指派其他角色再刪除。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        await roles.DeleteAsync(roleId, ct);
        await opLog.LogAsync(scope, "merchant.role.delete", "app_role", roleId.ToString(),
            $"刪除了場館自訂角色「{role.Name}」（{role.Code}）", before: new { role.Code, role.Name }, ct: ct);
        return Results.NoContent();
    }

    /// <summary>
    /// 場館範圍的權限目錄（給角色管理頁畫 CRUD/子功能核取方塊用）——只回傳目錄本身
    /// （代碼/名稱/子功能定義），不是某個角色的授予狀態，那個要另外呼叫
    /// GetRolePermissions。先前只有平台範圍的 /platform/permissions，場館範圍完全沒有
    /// 對應端點，導致角色管理頁面沒辦法知道有哪些資源可以勾選。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="repo">權限目錄倉儲</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：場館可用權限資源目錄清單</returns>
    private static async Task<IResult> ListPermissionCatalog(
        ClaimsPrincipal principal, PermissionRepository repo, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "read", out _)) return Results.Forbid();
        return Results.Ok(await repo.ListAsync(RoleScope.Merchant, ct));
    }

    /// <summary>
    /// 場館自訂角色的權限編輯：即使手動呼叫 API，任何 CRUD／子功能只要超過 merchant-admin
    /// 的實際授予（用未展開的細項真相比對，不受該場館簡化開關影響），一律拒絕寫入。
    /// </summary>
    /// <param name="principal">呼叫端身分識別 ClaimsPrincipal</param>
    /// <param name="roleId">目標角色 ID</param>
    /// <param name="requests">欲設定之權限項目陣列</param>
    /// <param name="roles">角色倉儲</param>
    /// <param name="grants">權限授予服務</param>
    /// <param name="opLog">操作稽核記錄器</param>
    /// <param name="ct">非同步作業取消權杖</param>
    /// <returns>HTTP 結果：成功回傳 204 NoContent</returns>
    private static async Task<IResult> SetRolePermissions(
        ClaimsPrincipal principal, int roleId, SetRolePermissionRequestItem[] requests,
        RoleRepository roles, PermissionGrantService grants, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "update", out var scope)) return Results.Forbid();

        var role = await roles.FindByIdAsync(roleId, ct);
        if (role is null || role.Scope != RoleScope.Merchant || role.MerchantId != scope!.MerchantId) return Results.NotFound();
        if (role.IsSystem) return Results.Forbid(); // 系統內建角色（merchant-admin/editor/viewer）僅平台管理員可改

        var merchantAdminRole = (await roles.ListAsync(RoleScope.Merchant, ct: ct))
            .FirstOrDefault(r => r.Code == "merchant-admin" && r.MerchantId is null);
        if (merchantAdminRole is null) return Results.Problem("找不到 merchant-admin 範本角色。", statusCode: StatusCodes.Status500InternalServerError);

        var ceiling = await grants.ForRolesAsync([merchantAdminRole.Id], ct);
        var ceilingByCode = ceiling.ToDictionary(g => g.Code, StringComparer.OrdinalIgnoreCase);

        foreach (var request in requests)
        {
            if (!ceilingByCode.TryGetValue(request.PermissionCode, out var allowed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [request.PermissionCode] = ["merchant-admin 未持有此資源，無法授予。"],
                });
            }
            var requested = new PermissionGrant(request.PermissionCode,
                [.. new[] { request.PerCreate ? "create" : null, request.PerRead ? "read" : null, request.PerUpdate ? "update" : null, request.PerDelete ? "delete" : null }.Where(a => a is not null).Cast<string>()],
                request.Options);
            var restricted = MerchantRolePermissionCeilingRules.RestrictGrants([requested], [allowed]).SingleOrDefault();
            var requestedActionCount = requested.Actions.Count + requested.Options.Count;
            var restrictedActionCount = (restricted?.Actions.Count ?? 0) + (restricted?.Options.Count ?? 0);
            if (restrictedActionCount < requestedActionCount)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [request.PermissionCode] = ["超過 merchant-admin 的權限上限，無法授予。"],
                });
            }
        }

        foreach (var request in requests)
        {
            await roles.SetRolePermissionAsync(roleId, request.PermissionCode,
                request.PerCreate, request.PerRead, request.PerUpdate, request.PerDelete, request.Options, ct);
        }

        await opLog.LogAsync(scope!, "merchant.role.permission.update", "app_role", roleId.ToString(),
            $"調整角色「{role.Name}」的權限設定（{requests.Length} 項資源）",
            after: requests, ct: ct);
        return Results.NoContent();
    }

    /// <summary>新增場館成員請求資料傳輸物件</summary>
    /// <param name="Username">使用者帳號</param>
    /// <param name="DisplayName">使用者姓名</param>
    /// <param name="Email">使用者電子郵件（可選）</param>
    /// <param name="Password">初始密碼（新建使用者時必填）</param>
    /// <param name="RoleId">指定指派之角色 ID（可選）</param>
    /// <param name="Features">指定勾選之功能代碼清單（可選）</param>
    public sealed record AddMerchantUserRequest(string Username, string DisplayName, string? Email, string? Password, int? RoleId, string[]? Features = null);

    /// <summary>更新場館成員請求資料傳輸物件</summary>
    /// <param name="RoleId">欲調整之角色 ID（可選）</param>
    /// <param name="IsActive">欲調整之帳號啟用狀態（可選）</param>
    /// <param name="DisplayName">欲調整之顯示名稱（可選）</param>
    public sealed record UpdateMerchantUserRequest(int? RoleId, bool? IsActive, string? DisplayName = null);

    /// <summary>建立場館角色請求資料傳輸物件</summary>
    /// <param name="Code">角色代碼</param>
    /// <param name="Name">角色名稱</param>
    public sealed record CreateMerchantRoleRequest(string Code, string Name);

    /// <summary>角色更名請求資料傳輸物件</summary>
    /// <param name="Name">新角色名稱</param>
    public sealed record RenameMerchantRoleRequest(string Name);

    /// <summary>設定角色細項權限項目資料傳輸物件</summary>
    /// <param name="PermissionCode">權限資源代碼</param>
    /// <param name="PerCreate">是否具備建立 (Create) 權限</param>
    /// <param name="PerRead">是否具備讀取 (Read) 權限</param>
    /// <param name="PerUpdate">是否具備更新 (Update) 權限</param>
    /// <param name="PerDelete">是否具備刪除 (Delete) 權限</param>
    /// <param name="Options">啟用的子功能選項代碼陣列</param>
    public sealed record SetRolePermissionRequestItem(string PermissionCode, bool PerCreate, bool PerRead, bool PerUpdate, bool PerDelete, string[] Options);
}

