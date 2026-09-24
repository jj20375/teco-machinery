using System.Text.Json;
using Dapper;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Domain.Permissions;

namespace Teco.Hvac.Infrastructure.Permissions;

/// <summary>
/// 由角色與 role_permissions 統一計算 JWT 及前端使用的 grants（用 Dapper 對應本專案的資料存取方式；
/// 核心演算法——場館簡化模式展開、merchant-admin 上限交集——放在 Teco.Hvac.Domain.Permissions
/// 供這裡呼叫，方便單獨測試）。
/// </summary>
public sealed class PermissionGrantService(TecoDbConnectionFactory factory)
{
    public async Task<IReadOnlyCollection<PermissionGrant>> ForPlatformUserAsync(AppUser user, CancellationToken ct = default)
    {
        if (user.SystemRoleId is int roleId) return await ForRolesAsync([roleId], ct);
        if (user.IsPlatformAdmin)
        {
            using var conn = await factory.CreateOpenAsync(ct);
            var platformAdminRoleId = await conn.ExecuteScalarAsync<int?>(
                "SELECT id FROM app_role WHERE scope = 0 AND code = 'platform-admin' AND merchant_id IS NULL");
            if (platformAdminRoleId is int id) return await ForRolesAsync([id], ct);
        }
        return [];
    }

    /// <summary>
    /// 計算場館 scope 的有效 grants：先算角色實際授予，依場館簡化開關展開，
    /// 最後與 merchant-admin 上限交集（即使資料或角色設定有誤，也不會讓人越權）。
    /// </summary>
    public async Task<IReadOnlyCollection<PermissionGrant>> ForMerchantAsync(
        MerchantMembership membership, Merchant merchant, CancellationToken ct = default)
    {
        var grants = membership.RoleId is int roleId ? await ForRolesAsync([roleId], ct) : [];
        grants = await ApplyMerchantPermissionConfigurationAsync(
            grants, merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled, ct);

        using var conn = await factory.CreateOpenAsync(ct);
        var merchantAdminRoleId = await conn.ExecuteScalarAsync<int?>(
            "SELECT id FROM app_role WHERE scope = 1 AND code = 'merchant-admin' AND merchant_id IS NULL");
        if (merchantAdminRoleId is not int adminRoleId) return [];

        var ceilingGrants = await ForRolesAsync([adminRoleId], ct);
        ceilingGrants = await ApplyMerchantPermissionConfigurationAsync(
            ceilingGrants, merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled, ct);

        return MerchantRolePermissionCeilingRules.RestrictGrants(grants, ceilingGrants);
    }

    /// <summary>取得場館簡化模式下的有效 grant；資料庫仍保留原本細項，只在算 grant 這一刻展開。</summary>
    private async Task<IReadOnlyCollection<PermissionGrant>> ApplyMerchantPermissionConfigurationAsync(
        IReadOnlyCollection<PermissionGrant> grants, bool isCrudEnabled, bool isOptionEnabled, CancellationToken ct)
    {
        if (grants.Count == 0 || (isCrudEnabled && isOptionEnabled)) return grants;

        var optionsByCode = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        if (!isOptionEnabled)
        {
            using var conn = await factory.CreateOpenAsync(ct);
            var rows = await conn.QueryAsync<(string Code, string SubFeaturesJson)>(
                "SELECT code AS Code, sub_features_json AS SubFeaturesJson FROM app_permission");
            optionsByCode = rows.ToDictionary(
                r => r.Code, r => ReadSubFeatureKeys(r.SubFeaturesJson), StringComparer.OrdinalIgnoreCase);
        }

        return grants
            .Select(grant => MerchantRolePermissionConfigurationRules.Apply(
                grant, isCrudEnabled, isOptionEnabled, optionsByCode.GetValueOrDefault(grant.Code) ?? []))
            .OrderBy(value => value.Code, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 計算給定角色的有效 grants。<c>is_full_access</c> 角色（platform-admin／merchant-admin）
    /// 不讀 <c>app_role_permission</c> 的既有資料列，一律即時算成「目前權限目錄裡同 scope 的
    /// 全部權限＋全部子功能」——這是刻意的設計，不是遺漏：先前這兩個角色的完整權限完全靠
    /// Seeder／migration 手動寫入 role_permission，每次權限目錄新增資源（例如
    /// hvac.thresholds/hvac.floor_plan）都要記得回頭幫這兩個角色補一行，漏補就會讓管理員角色
    /// 反而缺權限。改成動態計算後，未來新增權限只要寫進 app_permission，管理員角色自動涵蓋，
    /// 不必再補 migration。一般角色仍照舊只讀 role_permission 的實際授予。
    /// </summary>
    public async Task<IReadOnlyCollection<PermissionGrant>> ForRolesAsync(IReadOnlyCollection<int> roleIds, CancellationToken ct = default)
    {
        if (roleIds.Count == 0) return [];

        using var conn = await factory.CreateOpenAsync(ct);
        var roleRows = (await conn.QueryAsync<RoleScopeRow>(
            "SELECT id AS Id, scope AS Scope, is_full_access AS IsFullAccess FROM app_role WHERE id IN @roleIds",
            new { roleIds })).ToList();

        var fullAccessScopes = roleRows.Where(r => r.IsFullAccess).Select(r => r.Scope).Distinct().ToArray();
        var normalRoleIds = roleRows.Where(r => !r.IsFullAccess).Select(r => r.Id).ToArray();

        var grantsByCode = new Dictionary<string, (HashSet<string> Actions, HashSet<string> Options)>(StringComparer.OrdinalIgnoreCase);

        if (fullAccessScopes.Length > 0)
        {
            var permissionRows = await conn.QueryAsync<FullAccessPermissionRow>(
                "SELECT code AS Code, sub_features_json AS SubFeaturesJson FROM app_permission WHERE scope IN @fullAccessScopes",
                new { fullAccessScopes });
            foreach (var row in permissionRows)
            {
                var entry = GetOrAdd(grantsByCode, row.Code);
                entry.Actions.UnionWith(["create", "read", "update", "delete"]);
                entry.Options.UnionWith(ReadSubFeatureKeys(row.SubFeaturesJson));
            }
        }

        if (normalRoleIds.Length > 0)
        {
            var rows = await conn.QueryAsync<RolePermissionRow>(
                """
                SELECT p.code AS Code, rp.per_create AS PerCreate, rp.per_read AS PerRead,
                       rp.per_update AS PerUpdate, rp.per_delete AS PerDelete, rp.options_json AS OptionsJson
                FROM app_role_permission rp
                JOIN app_permission p ON p.id = rp.permission_id
                WHERE rp.role_id IN @normalRoleIds
                """,
                new { normalRoleIds });
            foreach (var row in rows)
            {
                var entry = GetOrAdd(grantsByCode, row.Code);
                if (row.PerCreate) entry.Actions.Add("create");
                if (row.PerRead) entry.Actions.Add("read");
                if (row.PerUpdate) entry.Actions.Add("update");
                if (row.PerDelete) entry.Actions.Add("delete");
                entry.Options.UnionWith(ReadSubFeatureKeys(row.OptionsJson));
            }
        }

        return grantsByCode
            .Select(kv => new PermissionGrant(kv.Key, kv.Value.Actions.ToArray(), kv.Value.Options.ToArray()))
            .OrderBy(g => g.Code, StringComparer.Ordinal)
            .ToArray();
    }

    private static (HashSet<string> Actions, HashSet<string> Options) GetOrAdd(
        Dictionary<string, (HashSet<string> Actions, HashSet<string> Options)> map, string code)
    {
        if (!map.TryGetValue(code, out var entry))
        {
            entry = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
            map[code] = entry;
        }
        return entry;
    }

    private static IReadOnlyCollection<string> ReadSubFeatureKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Select(value => value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Object when value.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String => key.GetString(),
                    _ => null,
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return []; // 損毀或未知格式一律 fail-closed，回傳空集合
        }
    }

    private sealed class RolePermissionRow
    {
        public required string Code { get; init; }
        public bool PerCreate { get; init; }
        public bool PerRead { get; init; }
        public bool PerUpdate { get; init; }
        public bool PerDelete { get; init; }
        public string? OptionsJson { get; init; }
    }

    private sealed class RoleScopeRow
    {
        public int Id { get; init; }
        public int Scope { get; init; }
        public bool IsFullAccess { get; init; }
    }

    private sealed class FullAccessPermissionRow
    {
        public required string Code { get; init; }
        public string? SubFeaturesJson { get; init; }
    }
}
