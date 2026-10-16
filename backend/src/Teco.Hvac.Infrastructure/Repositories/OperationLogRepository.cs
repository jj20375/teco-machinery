using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 操作紀錄查詢資料列模型。
/// class＋init 屬性，不用 positional record——理由見 MembershipRepository.MerchantUserRow 上的註解。
/// </summary>
public sealed class OperationLogRow
{
    /// <summary>紀錄流水號 PK。</summary>
    public required long Id { get; init; }
    /// <summary>建立時間戳記 (UTC)。</summary>
    public required DateTime CreatedAtUtc { get; init; }
    /// <summary>操作者帳號名稱。</summary>
    public required string ActorUsername { get; init; }
    /// <summary>操作者顯示名稱。</summary>
    public required string ActorDisplayName { get; init; }
    /// <summary>操作動作代碼（例如 "AUTH_LOGIN", "UPDATE_THRESHOLD"）。</summary>
    public required string Action { get; init; }
    /// <summary>操作摘要描述（中文）。</summary>
    public required string Summary { get; init; }
    /// <summary>操作是否成功執行。</summary>
    public bool IsSuccess { get; init; }
    /// <summary>來源客戶端 IP 位址。</summary>
    public string? Ip { get; init; }
}

/// <summary>
/// 系統審計與操作日誌資料存取倉儲。
/// 只負責 operation_log 的讀寫；組裝內容（action/summary/before-after）的責任在呼叫端的
/// OperationLogger，這裡刻意保持單純。
/// </summary>
/// <param name="factory">資料庫連線工廠，用於管理 MariaDB 連線生命週期。</param>
public sealed class OperationLogRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 新增一筆操作日誌紀錄。
    /// created_at 一定要在這裡明確帶 UTC 時間寫入，不能讓欄位吃 MariaDB 的
    /// DEFAULT CURRENT_TIMESTAMP——容器設了 TZ=Asia/Taipei，DEFAULT 會存成台北當地時間，
    /// 跟其他時序表（chiller_reading/fcu_reading，註解明寫「時間範圍皆為 UTC」）不一致，
    /// 之後用 UTC 區間查詢會全部對不上。
    /// </summary>
    /// <param name="log">操作日誌實體物件。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    public async Task CreateAsync(OperationLog log, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            """
            INSERT INTO operation_log
                (merchant_id, user_id, actor_username, actor_display_name, action, target_type, target_id,
                 summary, before_json, after_json, is_success, error_message, ip, created_at)
            VALUES
                (@MerchantId, @UserId, @ActorUsername, @ActorDisplayName, @Action, @TargetType, @TargetId,
                 @Summary, @BeforeJson, @AfterJson, @IsSuccess, @ErrorMessage, @Ip, @CreatedAtUtc)
            """, new
            {
                log.MerchantId, log.UserId, log.ActorUsername, log.ActorDisplayName, log.Action, log.TargetType,
                log.TargetId, log.Summary, log.BeforeJson, log.AfterJson, log.IsSuccess, log.ErrorMessage, log.Ip,
                CreatedAtUtc = log.CreatedAtUtc.UtcDateTime,
            });
    }

    /// <summary>
    /// 場館操作紀錄列表——merchantId 為 null 時只回傳 platform-scope 的紀錄（目前沒有平台端的
    /// 列表頁在用，先保留這個彈性）。fromUtc/toUtc 皆為 UTC，呼叫端負責從使用者輸入轉換。
    /// </summary>
    /// <param name="merchantId">目標場館編號。</param>
    /// <param name="fromUtc">起算時間 (UTC)。</param>
    /// <param name="toUtc">截止時間 (UTC)。</param>
    /// <param name="limit">最大回傳筆數。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>操作日誌清單，按時間降冪排序。</returns>
    public async Task<IReadOnlyList<OperationLogRow>> ListForMerchantAsync(
        int merchantId, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<OperationLogRow>(
            """
            SELECT id AS Id, created_at AS CreatedAtUtc, actor_username AS ActorUsername,
                   actor_display_name AS ActorDisplayName, action AS Action, summary AS Summary,
                   is_success AS IsSuccess, ip AS Ip
            FROM operation_log
            WHERE merchant_id = @merchantId AND created_at BETWEEN @fromUtc AND @toUtc
            ORDER BY created_at DESC
            LIMIT @limit
            """, new { merchantId, fromUtc, toUtc, limit });
        return rows.ToList();
    }
}
