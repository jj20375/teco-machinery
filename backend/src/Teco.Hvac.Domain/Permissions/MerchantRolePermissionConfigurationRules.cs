namespace Teco.Hvac.Domain.Permissions;

/// <summary>
/// 場館角色權限簡化展開純規則 (<see cref="MerchantRolePermissionConfigurationRules"/>)。
/// 
/// 【設計目標】
/// 依據場館當前選擇的權限配置簡化模式（是否開放細部 CRUD 勾選、是否開放細部 Option 勾選），
/// 動態展開角色的「最終有效權限」。
/// 資料庫內部仍完整保留細項設定以供未來還原，此處僅於「簽發 JWT 權杖」當下計算有效權限。
/// </summary>
public static class MerchantRolePermissionConfigurationRules
{
    /// <summary>完整的四項標準 CRUD 動作清單（建立、讀取、更新、刪除）。</summary>
    private static readonly string[] FullCrudActions = ["create", "read", "update", "delete"];

    /// <summary>
    /// 依據場館之簡化模式開關，對單一資源的權限授予進行動態展開或保留細項設定。
    /// 若場館關閉細部 CRUD 配置，只要該功能被授予即自動展開為擁有 Full CRUD；
    /// 若關閉細部選項配置，只要功能被授予即自動展開為擁有該資源的所有附加功能。
    /// </summary>
    /// <param name="grant">原始授予的權限設定物件。</param>
    /// <param name="isCrudConfigurationEnabled">場館是否開啟細部 CRUD 動作配置模式。</param>
    /// <param name="isOptionConfigurationEnabled">場館是否開啟細部附加子功能配置模式。</param>
    /// <param name="availableOptions">該資源系統所定義之全部可用子功能清單。</param>
    /// <returns>經簡化規則動態展開後的全新 <see cref="PermissionGrant"/> 物件實例。</returns>
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
