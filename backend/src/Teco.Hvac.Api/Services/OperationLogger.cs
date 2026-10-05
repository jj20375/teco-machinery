using System.Text.Json;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Services;

/// <summary>
/// 寫入 operation_log 的唯一入口。所有會被稽核的動作都透過這裡寫，不要在各 endpoint handler
/// 各自兜寫法——否則容易漏寫，欄位組裝方式也會慢慢分歧。失敗的操作也要記（is_success=false），
/// 對資安追蹤（例如權限不足被拒、密碼被鎖無法重設）一樣有價值。
/// </summary>
public sealed class OperationLogger(OperationLogRepository repo, UserRepository users, IHttpContextAccessor httpContextAccessor)
{
    /// <summary>
    /// 寫過紀錄就在這次請求上做記號：DeniedRequestAudit 只補記「沒人記過」的 403，
    /// 已經由 handler 寫了更詳細說明的拒絕不會重複記一筆。
    /// </summary>
    public const string LoggedMarker = "OperationLogged";

    private void MarkLogged() { if (httpContextAccessor.HttpContext is { } ctx) ctx.Items[LoggedMarker] = true; }

    private static string Cut(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];

    public async Task LogAsync(
        RequestScope scope, string action, string targetType, string targetId, string summary,
        object? before = null, object? after = null, bool isSuccess = true, string? errorMessage = null,
        CancellationToken ct = default)
    {
        // 操作者身分在寫入當下就查一次快照下來，之後這個人改名/停用/被刪，這筆紀錄的顯示不會跟著變。
        var actor = await users.FindByIdAsync(scope.UserId, ct);

        await repo.CreateAsync(new OperationLog
        {
            MerchantId = scope.MerchantId,
            UserId = scope.UserId,
            ActorUsername = actor?.Username ?? "(unknown)",
            ActorDisplayName = actor?.DisplayName ?? "(unknown)",
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Summary = summary,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after),
            IsSuccess = isSuccess,
            ErrorMessage = errorMessage,
            Ip = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        }, ct);
        MarkLogged();
    }

    /// <summary>
    /// 登入、登入失敗、帳號鎖定：這時候還沒有 JWT（沒有 RequestScope），操作者資訊由呼叫端直接給。
    /// 帳號不存在的失敗登入 userId 給 0、username 放對方輸入的帳號（截短）。
    /// </summary>
    public async Task LogAuthAsync(
        int userId, string username, string displayName, int? merchantId, string action, string summary,
        bool isSuccess, string? errorMessage = null, CancellationToken ct = default)
    {
        await repo.CreateAsync(new OperationLog
        {
            MerchantId = merchantId,
            UserId = userId,
            ActorUsername = Cut(username, 64),
            ActorDisplayName = Cut(displayName, 64),
            Action = action,
            TargetType = "app_user",
            TargetId = userId.ToString(),
            Summary = Cut(summary, 255),
            IsSuccess = isSuccess,
            ErrorMessage = errorMessage is null ? null : Cut(errorMessage, 255),
            Ip = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        }, ct);
        MarkLogged();
    }

    /// <summary>被拒絕的操作（業務規則擋下、權限不足）也要留下紀錄，對資安追蹤一樣有價值。</summary>
    public Task LogDeniedAsync(
        RequestScope scope, string action, string targetType, string targetId, string summary, string reason,
        CancellationToken ct = default) =>
        LogAsync(scope, action, targetType, targetId, Cut(summary, 255), isSuccess: false, errorMessage: Cut(reason, 255), ct: ct);
}
