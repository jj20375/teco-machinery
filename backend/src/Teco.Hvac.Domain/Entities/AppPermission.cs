namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 資源、CRUD 與子功能組成的權限目錄項目（比照美達特 PlatformPermission）。
/// CRUD 由 AppRolePermission 的獨立欄位表示，不併入 Code。
/// </summary>
public sealed class AppPermission
{
    public int Id { get; set; }

    /// <summary>權限代碼，例如 hvac.chillers、platform.merchants。</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public required RoleScope Scope { get; set; }

    /// <summary>權限樹的父節點，供 CMS 選單分群顯示；不影響授權判斷。</summary>
    public int? ParentId { get; set; }
    public int SortOrder { get; set; }
    public bool IsMenu { get; set; }
    public string? RoutePath { get; set; }

    /// <summary>子功能定義 JSON，例如 [{"key":"ack","name":"確認告警"}]。</summary>
    public string SubFeaturesJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>子功能鍵值與顯示名稱，對應 SubFeaturesJson 的單一項目。</summary>
public sealed record SubFeature(string Key, string Name);
