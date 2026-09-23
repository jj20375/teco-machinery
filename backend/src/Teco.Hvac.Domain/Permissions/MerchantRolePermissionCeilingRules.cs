namespace Teco.Hvac.Domain.Permissions;

/// <summary>
/// 場館角色權限上限的純規則（比照美達特 CustomerRolePermissionCeilingRules）：
/// 所有場館自訂角色算出的 grant，最終都不得超過 merchant-admin 實際持有的 CRUD 與子功能。
/// 這是 JWT 簽發前的最後一道 fail-closed 防線，即使資料或邏輯有 bug 也不會讓人越權。
/// </summary>
public static class MerchantRolePermissionCeilingRules
{
    public static IReadOnlyCollection<PermissionGrant> RestrictGrants(
        IEnumerable<PermissionGrant> requested,
        IEnumerable<PermissionGrant> ceiling)
    {
        var ceilingByCode = ceiling.ToDictionary(value => value.Code, StringComparer.OrdinalIgnoreCase);
        return requested
            .Where(value => ceilingByCode.ContainsKey(value.Code))
            .Select(value =>
            {
                var allowed = ceilingByCode[value.Code];
                return new PermissionGrant(
                    value.Code,
                    value.Actions.Intersect(allowed.Actions, StringComparer.Ordinal).ToArray(),
                    value.Options.Intersect(allowed.Options, StringComparer.Ordinal).ToArray());
            })
            .Where(value => value.Actions.Count > 0 || value.Options.Count > 0)
            .OrderBy(value => value.Code, StringComparer.Ordinal)
            .ToArray();
    }
}
