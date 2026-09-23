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
    }
}
