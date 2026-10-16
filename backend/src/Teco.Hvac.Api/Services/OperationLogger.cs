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
/// <param name="repo">操作日誌倉儲，負責資料庫持久化。</param>
/// <param name="users">使用者倉儲，用於查詢操作當下的使用者身分快照。</param>
/// <param name="httpContextAccessor">HTTP 上下文存取器，用於解析客戶端 IP 與標記重複稽核旗標。</param>
public sealed class OperationLogger(OperationLogRepository repo, UserRepository users, IHttpContextAccessor httpContextAccessor)
{
    /// <summary>
    /// 寫過紀錄就在這次請求上做記號：DeniedRequestAudit 只補記「沒人記過」的 403，
    /// 已經由 handler 寫了更詳細說明的拒絕不會重複記一筆。
    /// </summary>
    public const string LoggedMarker = "OperationLogged";

    /// <summary>在目前請求的 HttpContext.Items 中加入已記錄標記。</summary>
    private void MarkLogged() { if (httpContextAccessor.HttpContext is { } ctx) ctx.Items[LoggedMarker] = true; }

    /// <summary>字串長度截斷輔助函式，避免超出資料庫欄位限制。</summary>
    /// <param name="value">原始字串。</param>
    /// <param name="max">允許之最大長度。</param>
    /// <returns>截斷後之安全字串。</returns>
    private static string Cut(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];

    /// <summary>
    /// 記錄一般已認證請求的業務操作日誌。
    /// </summary>
    /// <param name="scope">當前使用者的請求權限範圍。</param>
    /// <param name="action">動作代碼（例如 "hvac.threshold.chiller.update"）。</param>
    /// <param name="targetType">操作對象資源類型（例如 "chiller", "fcu"）。</param>
    /// <param name="targetId">操作對象識別碼。</param>
    /// <param name="summary">操作摘要描述。</param>
    /// <param name="before">異動前的物件狀態（將序列化為 JSON）。</param>
    /// <param name="after">異動後的物件狀態（將序列化為 JSON）。</param>
    /// <param name="isSuccess">操作是否成功執行，預設為 true。</param>
    /// <param name="errorMessage">失敗時的錯誤原因訊息。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
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
    /// 記錄認證相關事件（登入、登出、登入失敗、帳號鎖定）。
    /// 登入、登入失敗、帳號鎖定：這時候還沒有 JWT（沒有 RequestScope），操作者資訊由呼叫端直接給。
    /// 帳號不存在的失敗登入 userId 給 0、username 放對方輸入的帳號（截短）。
    /// </summary>
    /// <param name="userId">使用者識別碼（若帳號不存在則為 0）。</param>
    /// <param name="username">使用者登入帳號。</param>
    /// <param name="displayName">使用者顯示名稱。</param>
    /// <param name="merchantId">所屬場館 ID（若無則為 null）。</param>
    /// <param name="action">動作代碼。</param>
    /// <param name="summary">摘要文字說明。</param>
    /// <param name="isSuccess">驗證是否成功。</param>
    /// <param name="errorMessage">失敗錯誤訊息。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
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

    /// <summary>
    /// 記錄被業務規則或授權守衛主動拒絕的操作日誌。
    /// 被拒絕的操作（業務規則擋下、權限不足）也要留下紀錄，對資安追蹤一樣有價值。
    /// </summary>
    /// <param name="scope">當前使用者的請求權限範圍。</param>
    /// <param name="action">動作代碼。</param>
    /// <param name="targetType">操作對象資源類型。</param>
    /// <param name="targetId">操作對象識別碼。</param>
    /// <param name="summary">操作摘要描述。</param>
    /// <param name="reason">拒絕原因說明。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    public Task LogDeniedAsync(
        RequestScope scope, string action, string targetType, string targetId, string summary, string reason,
        CancellationToken ct = default) =>
        LogAsync(scope, action, targetType, targetId, Cut(summary, 255), isSuccess: false, errorMessage: Cut(reason, 255), ct: ct);
}
