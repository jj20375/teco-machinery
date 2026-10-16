namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 系統操作稽核日誌實體 (<see cref="OperationLog"/>)。
/// 對應資料庫 operation_log 資料表。
/// 
/// 【不可變稽核設計】
/// 1. 快照原則：ActorUsername 與 ActorDisplayName 於寫入當下直接快照，不透過外鍵關聯使用者資料表；
///    即便該使用者未來修改名稱、被停用或刪除，過往歷史日誌中的操作者資訊亦絕對不可變更。
/// 2. 語意摘要：Summary 於寫入時即組裝成清晰完整的繁體中文句子，查詢端直接渲染即可，無需重複編寫拼裝邏輯。
/// 3. 多租戶隔離：MerchantId 為 null 代表平台全域管理動作（如新增場館）；若為特定場館操作則必須填入 MerchantId，以確保場館間日誌精確隔離。
/// </summary>
public sealed class OperationLog
{
    /// <summary>操作日誌主鍵識別碼 (Primary Key)。</summary>
    public long Id { get; set; }

    /// <summary>操作所屬之場館 ID；若為跨場館之平台系統操作則為 null。</summary>
    public int? MerchantId { get; set; }

    /// <summary>執行該操作之使用者 ID。</summary>
    public required int UserId { get; set; }

    /// <summary>操作者帳號名稱快照（例如 "admin"）。</summary>
    public required string ActorUsername { get; set; }

    /// <summary>操作者中文顯示名稱快照（例如 "張系統管理員"）。</summary>
    public required string ActorDisplayName { get; set; }

    /// <summary>操作動作代碼（例如 "create"、"update"、"delete"、"login"、"ack_alarm"）。</summary>
    public required string Action { get; set; }

    /// <summary>受操作目標之資源類型（例如 "user"、"role"、"alarm_rule"、"device_placement"）。</summary>
    public required string TargetType { get; set; }

    /// <summary>受操作目標之識別代碼或 ID（例如 "102"、"CH-01"）。</summary>
    public required string TargetId { get; set; }

    /// <summary>操作內容之繁體中文完整描述摘要（例如 "修改了 1號冰水主機 的溫度告警門檻"）。</summary>
    public required string Summary { get; set; }

    /// <summary>變更前之物件狀態快照 JSON 字串；若為新增操作或無異動前資料則為 null。</summary>
    public string? BeforeJson { get; set; }

    /// <summary>變更後之物件狀態快照 JSON 字串；若為刪除操作則為 null。</summary>
    public string? AfterJson { get; set; }

    /// <summary>該操作執行結果是否成功（true 代表成功，false 代表被拒絕或發生錯誤）。</summary>
    public bool IsSuccess { get; set; } = true;

    /// <summary>當操作失敗或被拒絕時，記錄之錯誤訊息或拒絕原因；成功時為 null。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>發起該操作請求之客戶端 IP 位址。</summary>
    public string? Ip { get; set; }

    /// <summary>操作紀錄產生之標準 UTC 時間戳記。</summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
