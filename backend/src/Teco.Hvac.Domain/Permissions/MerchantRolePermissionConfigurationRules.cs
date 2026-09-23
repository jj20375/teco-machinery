namespace Teco.Hvac.Domain.Permissions;

/// <summary>
/// 依場館選擇的簡化模式展開角色的有效權限（比照美達特 CustomerRolePermissionConfigurationRules）。
/// 資料庫保留細項設定不變，這裡只影響「發 JWT 那一刻」算出的有效 grant，讓場館之後隨時能切回細項模式。
/// </summary>
public static class MerchantRolePermissionConfigurationRules
{
    private static readonly string[] FullCrudActions = ["create", "read", "update", "delete"];

    /// <summary>
    /// 關閉 CRUD 或附加功能細項時，資源只要被授予（Actions/Options 非空）就分別展開成完整動作
    /// 或該資源已定義的所有附加功能。這正是「一開功能就有完整 CRUD」的實作點。
    /// </summary>
    public static PermissionGrant Apply(
        PermissionGrant grant,
        bool isCrudConfigurationEnabled,
        bool isOptionConfigurationEnabled,
        IReadOnlyCollection<string> availableOptions)
    {
        return new PermissionGrant(
            grant.Code,
            isCrudConfigurationEnabled ? grant.Actions : FullCrudActions,
            isOptionConfigurationEnabled ? grant.Options : availableOptions);
    }
}
