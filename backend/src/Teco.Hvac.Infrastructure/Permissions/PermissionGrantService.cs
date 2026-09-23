using System.Text.Json;
using Dapper;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Domain.Permissions;

namespace Teco.Hvac.Infrastructure.Permissions;

/// <summary>
/// 由角色與 role_permissions 統一計算 JWT 及前端使用的 grants（比照美達特 PermissionGrantService，
/// 改用 Dapper 對應本專案的資料存取方式；核心演算法——場館簡化模式展開、merchant-admin 上限交集——
/// 完全沿用美達特已驗證過的規則，搬到 Teco.Hvac.Domain.Permissions 供這裡呼叫）。
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

    public async Task<IReadOnlyCollection<PermissionGrant>> ForRolesAsync(IReadOnlyCollection<int> roleIds, CancellationToken ct = default)
    {
        if (roleIds.Count == 0) return [];

        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<RolePermissionRow>(
            """
            SELECT p.code AS Code, rp.per_create AS PerCreate, rp.per_read AS PerRead,
                   rp.per_update AS PerUpdate, rp.per_delete AS PerDelete, rp.options_json AS OptionsJson
            FROM app_role_permission rp
            JOIN app_permission p ON p.id = rp.permission_id
            WHERE rp.role_id IN @roleIds
            """,
            new { roleIds });

        return rows.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PermissionGrant(
                group.Key,
                group.SelectMany(r => new[]
                {
                    r.PerCreate ? "create" : null,
                    r.PerRead ? "read" : null,
                    r.PerUpdate ? "update" : null,
                    r.PerDelete ? "delete" : null,
                }.Where(a => a is not null).Cast<string>()).Distinct(StringComparer.Ordinal).ToArray(),
                group.SelectMany(r => ReadSubFeatureKeys(r.OptionsJson)).Distinct(StringComparer.Ordinal).ToArray()))
            .OrderBy(g => g.Code, StringComparer.Ordinal)
            .ToArray();
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
}
