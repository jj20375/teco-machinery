namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 場館／商家主檔（不做「一商家一資料庫」——
/// TECO 目前的多租戶需求是「同集團的其他場館」，用單庫 + MerchantId 範圍隔離即可，
/// 不需要跨公司資料實體隔離與 provisioning 流程）。
/// </summary>
public sealed class Merchant
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }

    /// <summary>active｜suspended。沒有 provisioning 狀態——TECO 不用單獨建資料庫。</summary>
    public string Status { get; set; } = "active";

    /// <summary>
    /// 場館是否使用 CRUD 細項角色權限設定；停用時（false）授予資源即自動取得完整 CRUD。
    /// 只有平台管理員能改。
    /// </summary>
    public bool IsRoleCrudConfigurationEnabled { get; set; } = true;

    /// <summary>場館是否使用附加功能細項角色權限設定；停用時授予資源即自動取得該資源全部附加功能。</summary>
    public bool IsRoleOptionConfigurationEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
