namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 操作稽核紀錄。ActorUsername/ActorDisplayName 在寫入當下就快照，不靠 JOIN app_user 反查——
/// 使用者之後改名、停用或被刪除，舊紀錄的操作者欄位都不該跟著變。Summary 也是寫入當下組好的
/// 人話句子，避免讀取端要為每一種 Action 各自兜組句邏輯。MerchantId 為 null 代表 platform-scope
/// 操作（例如建立商家）；merchant-scope 操作一定要帶值，查詢才能正確依場館隔離。
/// </summary>
public sealed class OperationLog
{
    public long Id { get; set; }
    public int? MerchantId { get; set; }
    public required int UserId { get; set; }
    public required string ActorUsername { get; set; }
    public required string ActorDisplayName { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string Summary { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public bool IsSuccess { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public string? Ip { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
