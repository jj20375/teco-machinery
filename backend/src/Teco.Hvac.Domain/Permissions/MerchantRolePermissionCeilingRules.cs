namespace Teco.Hvac.Domain.Permissions;

/// <summary>
/// 場館角色權限天花板限制純規則 (<see cref="MerchantRolePermissionCeilingRules"/>)。
/// 
/// 【安全架構設計】
/// 任何場館自訂角色（如「一般操作員」、「值班工程師」）所計算出的權限授予 (Grant)，
/// 其最終可執行的 CRUD 動作與附加子功能，絕對不得超越該場館管理員角色 (merchant-admin) 所實際持有的上限 (Ceiling)。
/// 這是 JWT 簽發前的最後一道 fail-closed 核心防線，即使底層資料庫或角色配置邏輯存在缺陷，亦能確保租戶內角色絕無可能越權。
/// </summary>
public static class MerchantRolePermissionCeilingRules
{
    /// <summary>
    /// 將使用者請求的權限授予集合與天花板權限集合進行交集限制，裁決出最終有效的安全權限集合。
    /// </summary>
    /// <param name="requested">使用者所屬角色或請求套用的原始權限授予集合。</param>
    /// <param name="ceiling">場館管理員 (merchant-admin) 所持有的權限天花板集合。</param>
    /// <returns>經天花板安全限制裁決後、已排除越權動作之最終有效權限集合。</returns>
    public static IReadOnlyCollection<PermissionGrant> RestrictGrants(
        IEnumerable<PermissionGrant> requested,
        IEnumerable<PermissionGrant> ceiling)
    {
        // 建立天花板代碼字典，加速後續查詢比對
        var ceilingByCode = ceiling.ToDictionary(value => value.Code, StringComparer.OrdinalIgnoreCase);

        return requested
            // 1. 僅保留天花板中所包含的權限資源代碼（排除天花板未開放之全新資源）
            .Where(value => ceilingByCode.ContainsKey(value.Code))
            .Select(value =>
            {
                var allowed = ceilingByCode[value.Code];
                // 2. 對 CRUD 動作與子功能分別取交集，任何超出天花板的權限均被自動過濾
                return new PermissionGrant(
                    value.Code,
                    value.Actions.Intersect(allowed.Actions, StringComparer.Ordinal).ToArray(),
                    value.Options.Intersect(allowed.Options, StringComparer.Ordinal).ToArray());
            })
            // 3. 過濾掉交集後動作與子選項皆為空的無效授予
            .Where(value => value.Actions.Count > 0 || value.Options.Count > 0)
            .OrderBy(value => value.Code, StringComparer.Ordinal)
            .ToArray();
    }
}
