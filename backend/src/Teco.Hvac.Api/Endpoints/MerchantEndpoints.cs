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
    public static void MapMerchantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/merchant").RequireAuthorization();

        group.MapGet("/users", ListUsers);
        group.MapPost("/users", AddUser);
        group.MapPatch("/users/{membershipId:int}", UpdateUser);
        group.MapDelete("/users/{membershipId:int}", DeleteUser);
        group.MapPost("/users/{membershipId:int}/reset-password", ResetUserPassword);
        group.MapGet("/users/{membershipId:int}/features", GetUserFeatures);
        group.MapPut("/users/{membershipId:int}/features", SetUserFeatures);

        group.MapGet("/roles", ListRoles);
        group.MapPost("/roles", CreateRole);
        group.MapPatch("/roles/{roleId:int}", RenameRole);
        group.MapDelete("/roles/{roleId:int}", DeleteRole);
        group.MapGet("/roles/{roleId:int}/permissions", GetRolePermissions);
        group.MapPost("/roles/{roleId:int}/permissions", SetRolePermissions);
        group.MapGet("/permissions", ListPermissionCatalog);

        group.MapGet("/operation-logs", ListOperationLogs);
    }

    /// <summary>action 代碼 → 畫面「操作類別」欄的分類標籤，跟 summary（人話句子）分開存放的原因
    /// 見 OperationLog 實體上的註解。找不到對照時直接顯示代碼本身，不擋畫面。</summary>
    private static readonly Dictionary<string, string> ActionCategoryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["merchant.user.create"] = "使用者異動",
        ["merchant.user.update"] = "使用者異動",
        ["merchant.user.reset_password"] = "重設密碼",
        ["merchant.user.features.update"] = "使用者異動",
        ["merchant.role.create"] = "角色設定",
        ["merchant.role.permission.update"] = "角色設定",
        ["auth.change_password"] = "重設密碼",
        ["alarm.ack"] = "告警處理",
    };

    private const int MaxOperationLogRangeDays = 366; // 對齊畫面「最大範圍：一年」的查詢限制。

    /// <summary>
    /// 場館操作紀錄查詢。from/to 未帶時區資訊時，ASP.NET Core 的 DateTime binder 會先解成
    /// Unspecified kind，.ToUniversalTime() 再依容器的 TZ=Asia/Taipei 換算成 UTC——
    /// 跟 ChillerEndpoints/FcuEndpoints 的 history 查詢是同一套慣例。
    /// </summary>
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
            actionType = ActionCategoryLabels.GetValueOrDefault(r.Action, r.Action),
            content = r.Summary,
            ipAddress = r.Ip,
            isSuccess = r.IsSuccess,
        }));
    }

    /// <summary>
    /// 畫面上「編輯成員」核取方塊對應的頁面級資源，順序即畫面顯示順序。
    /// 原本只有六項（對齊 Figma），新增 hvac.floor_plan 後變七項，跟側邊欄的七個項目 1:1 對應——
    /// 少了這一項的話，用個人權限（member-{id} 專屬角色）的使用者永遠拿不到空間設備配置權限。
    /// </summary>
    private static readonly string[] MemberFeatureCodes =
    [
        "hvac.overview", "hvac.floor_plan", "hvac.chillers", "hvac.fcus", "hvac.reports",
        "merchant.users", "merchant.operation_log",
    ];

    private static bool RequireMerchant(ClaimsPrincipal principal, string code, string action, out RequestScope? scope)
    {
        scope = null;
        if (!RequestScope.TryRead(principal, out scope) || scope is null) return false;
        return !scope.IsPlatform && scope.MerchantId is not null && scope.Has(code, action);
    }

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
    private static async Task<IResult> AddUser(
        ClaimsPrincipal principal, AddMerchantUserRequest request,
        UserRepository users, MembershipRepository memberships, RoleRepository roles, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.users", "create", out var scope)) return Results.Forbid();

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["帳號與使用者名稱為必填。"] });
        }

        var role = await roles.FindByIdAsync(request.RoleId, ct);
        if (role is null || role.Scope != RoleScope.Merchant) return Results.BadRequest(new { message = "角色不存在或不是場館範圍角色。" });

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
            new MerchantMembership { MerchantId = scope!.MerchantId!.Value, UserId = userId, RoleId = request.RoleId }, ct);

        await opLog.LogAsync(scope, "merchant.user.create", "merchant_membership", id.ToString(),
            $"新增使用者「{request.DisplayName.Trim()}」（{request.Username.Trim()}），角色：{role.Name}",
            after: new { request.Username, request.DisplayName, request.Email, RoleId = request.RoleId, RoleName = role.Name }, ct: ct);
        return Results.Created($"/api/v1/merchant/users/{id}", new { id });
    }

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
                return MerchantMembershipGuard.Denied(denied);
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
            return Results.Json(new { message = "無法刪除自己的帳號。" }, statusCode: StatusCodes.Status400BadRequest);
        if (await MerchantMembershipGuard.CheckAsync(scope, membership, MemberAction.Delete, memberships, roles, ct) is { } denied)
            return MerchantMembershipGuard.Denied(denied);

        var targetUser = await users.FindByIdAsync(membership.UserId, ct);
        var targetName = targetUser?.DisplayName ?? $"membership#{membershipId}";

        var merchantUsers = await memberships.ListForMerchantAsync(scope.MerchantId.Value, ct);
        var activeAdminCount = merchantUsers.Count(u => u.RoleCode == "merchant-admin" && u.IsActive);
        var targetIsActiveAdmin = merchantUsers.Any(u => u.MembershipId == membershipId && u.RoleCode == "merchant-admin" && u.IsActive);
        if (targetIsActiveAdmin && activeAdminCount <= 1)
        {
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
            return MerchantMembershipGuard.Denied(denied);

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
    /// 「編輯成員」面板要顯示的六個頁面開關目前是否勾選——讀這個人目前指派角色的實際授予
    /// （不管是系統範本還是個人專屬角色都一樣讀），只看六個固定資源有沒有 per_read。
    /// </summary>
    private static async Task<IResult> GetUserFeatures(
        ClaimsPrincipal principal, int membershipId, MembershipRepository memberships, RoleRepository roles, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.users", "read", out var scope)) return Results.Forbid();
        var membership = await memberships.FindByIdAsync(membershipId, ct);
        if (membership is null || membership.MerchantId != scope!.MerchantId) return Results.NotFound();

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (membership.RoleId is int roleId)
        {
            var details = await roles.GetRolePermissionsAsync(roleId, ct);
            foreach (var d in details.Where(d => d.PerRead)) enabled.Add(d.PermissionCode);
        }
        return Results.Ok(new { features = MemberFeatureCodes.Where(enabled.Contains).ToArray() });
    }

    /// <summary>
    /// 儲存「編輯成員」的六個核取方塊：幫這個成員準備一個專屬角色（第一次會建立，之後沿用），
    /// 把該角色在六個固定資源上的 per_read 設成勾選狀態，並把 membership 指到這個專屬角色。
    ///
    /// 只給 read，不直接給 create/update/delete——這正是刻意的設計，不是偷懶：場館目前是
    /// 「CRUD 簡化模式」，read 在發 JWT 時會自動展開成完整 CRUD（見 MerchantRolePermissionConfigurationRules），
    /// 剛好對應這個畫面「只是勾選要不要用某個功能」的語意，細部 CRUD 交給角色系統自己算，
    /// 不需要在這裡重新發明一次。絕不直接改 merchant-admin/editor/viewer 這三個系統範本——
    /// 那是所有場館共用的全域範本，改了會波及其他場館。
    /// </summary>
    private static async Task<IResult> SetUserFeatures(
        ClaimsPrincipal principal, int membershipId, SetUserFeaturesRequest request,
        MembershipRepository memberships, RoleRepository roles, UserRepository users, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.users", "update", out var scope)) return Results.Forbid();
        var membership = await memberships.FindByIdAsync(membershipId, ct);
        if (membership is null || membership.MerchantId != scope!.MerchantId) return Results.NotFound();

        // 場館管理員絕對不能被這個面板轉成專屬角色——MemberFeatureCodes 只有六個固定資源，
        // 沒有 merchant.roles，一旦轉換，這個帳號會永久失去角色管理能力（實際發生過的事故：
        // 管理員自己在這個面板存檔後，隔天發現看不到「角色管理」，因為帳號已經不是 merchant-admin
        // 角色了）。要調整場館管理員的權限，本來就應該去角色管理改 merchant-admin 這個角色本身。
        if (membership.RoleId is int currentRoleId)
        {
            var currentRole = await roles.FindByIdAsync(currentRoleId, ct);
            if (currentRole is { IsSystem: true, Code: "merchant-admin" })
            {
                return Results.BadRequest(new
                {
                    message = "場館管理員的權限由角色本身決定，無法用此面板調整（會導致永久失去角色管理等未列在開關上的能力）。如需調整，請改到角色管理編輯 merchant-admin 角色。",
                });
            }
        }

        var personalRoleCode = $"member-{membershipId}";
        var personalRole = await roles.FindByCodeAsync(RoleScope.Merchant, personalRoleCode, scope.MerchantId, ct);
        int personalRoleId;
        if (personalRole is null)
        {
            personalRoleId = await roles.CreateAsync(new AppRole
            {
                Code = personalRoleCode,
                Name = "自訂權限",
                Scope = RoleScope.Merchant,
                MerchantId = scope.MerchantId,
                IsSystem = false,
            }, ct);
        }
        else
        {
            personalRoleId = personalRole.Id;
        }

        // 在真的動手改之前，先把這個專屬角色目前勾選了什麼記下來，才有 before 可以比對。
        var beforeDetails = await roles.GetRolePermissionsAsync(personalRoleId, ct);
        var beforeFeatures = beforeDetails.Where(d => d.PerRead).Select(d => d.PermissionCode).ToArray();

        var checkedFeatures = new HashSet<string>(request.Features ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var code in MemberFeatureCodes)
        {
            var isChecked = checkedFeatures.Contains(code);
            await roles.SetRolePermissionAsync(personalRoleId, code, false, isChecked, false, false, [], ct);
        }

        if (membership.RoleId != personalRoleId)
        {
            await memberships.UpdateRoleAsync(membershipId, personalRoleId, null, ct);
        }
        await users.IncrementAuthVersionAsync(membership.UserId, ct);

        var targetUser = await users.FindByIdAsync(membership.UserId, ct);
        var afterFeatures = MemberFeatureCodes.Where(checkedFeatures.Contains).ToArray();
        await opLog.LogAsync(scope, "merchant.user.features.update", "merchant_membership", membershipId.ToString(),
            $"調整了「{targetUser?.DisplayName ?? $"membership#{membershipId}"}」可使用的頁面權限",
            before: new { Features = beforeFeatures }, after: new { Features = afterFeatures }, ct: ct);
        return Results.NoContent();
    }

    public sealed record SetUserFeaturesRequest(string[]? Features);

    /// <summary>單一角色目前的細項授予——「編輯成員」面板用來預先勾選核取方塊。</summary>
    private static async Task<IResult> GetRolePermissions(
        ClaimsPrincipal principal, int roleId, RoleRepository roles, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "read", out _)) return Results.Forbid();
        return Results.Ok(await roles.GetRolePermissionsAsync(roleId, ct));
    }

    private static async Task<IResult> ListRoles(ClaimsPrincipal principal, RoleRepository repo, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "read", out var scope)) return Results.Forbid();
        return Results.Ok(await repo.ListAsync(RoleScope.Merchant, scope!.MerchantId, ct));
    }

    private static async Task<IResult> CreateRole(
        ClaimsPrincipal principal, CreateMerchantRoleRequest request, RoleRepository repo, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequireMerchant(principal, "merchant.roles", "create", out var scope)) return Results.Forbid();
        var id = await repo.CreateAsync(new AppRole
        {
            Code = request.Code.Trim(), Name = request.Name.Trim(), Scope = RoleScope.Merchant, MerchantId = scope!.MerchantId,
        }, ct);

        await opLog.LogAsync(scope, "merchant.role.create", "app_role", id.ToString(),
            $"建立場館自訂角色「{request.Name.Trim()}」（{request.Code.Trim()}）",
            after: new { request.Code, request.Name }, ct: ct);
        return Results.Created($"/api/v1/merchant/roles/{id}", new { id });
    }

    /// <summary>角色改名——只改顯示名稱，Code 建立後不能改（可能已經被其他地方引用比對）。</summary>
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

    public sealed record AddMerchantUserRequest(string Username, string DisplayName, string? Email, string? Password, int RoleId);
    public sealed record UpdateMerchantUserRequest(int? RoleId, bool? IsActive, string? DisplayName = null);
    public sealed record CreateMerchantRoleRequest(string Code, string Name);
    public sealed record RenameMerchantRoleRequest(string Name);
    public sealed record SetRolePermissionRequestItem(string PermissionCode, bool PerCreate, bool PerRead, bool PerUpdate, bool PerDelete, string[] Options);
}
