using System.Text.Json;
using Dapper;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Domain.Permissions;

namespace Teco.Hvac.Infrastructure.Permissions;

/// <summary>
/// 權限授予計算服務 (<see cref="PermissionGrantService"/>)。
/// 
/// 負責由資料庫中的角色與權限關聯表計算出簽發 JWT 以及提供給前端選單、按鈕防護所使用的有效權限清單 (<see cref="PermissionGrant"/>)。
/// 
/// 【核心安全性架構】
/// 1. 動態完全存取 (Full Access)：具有 IsFullAccess 標記的角色（如 platform-admin、merchant-admin）
///    直接動態展開為當前目錄下同 Scope 的全部權限與全部子功能，免除每次新增權限都要手動更新資料庫關聯的維護負擔。
/// 2. 場館簡化模式展開：依據場館之 IsRoleCrudConfigurationEnabled 與 IsRoleOptionConfigurationEnabled 設定動態展開權限。
/// 3. 天花板安全限制 (Fail-Closed Ceiling)：場館自訂角色之權限計算結果，最終一律與該場館 merchant-admin 之有效權限進行交集限制，
///    徹底防範租戶自訂角色發生越權操作。
/// </summary>
public sealed class PermissionGrantService(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 計算平台使用者（跨場館系統帳號）的有效權限集合。
    /// </summary>
    /// <param name="user">應用程式使用者實體物件。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>該使用者在平台 Scope 下之有效權限清單。</returns>
    public async Task<IReadOnlyCollection<PermissionGrant>> ForPlatformUserAsync(AppUser user, CancellationToken ct = default)
    {
        // 1. 若已指派具體的平台角色，直接計算該角色權限
        if (user.SystemRoleId is int roleId) return await ForRolesAsync([roleId], ct);

        // 2. 若為平台超級管理員但未指定特定角色，查詢系統內建之 platform-admin 角色計算權限
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
    /// 計算使用者在特定場館下的有效權限集合。
    /// 包含角色實際授予、依場館簡化開關展開，並經過 merchant-admin 天花板裁決限制。
    /// </summary>
    /// <param name="membership">使用者在該場館中的成員資格實體。</param>
    /// <param name="merchant">該場館主檔實體（包含 CRUD 與 Option 簡化配置開關）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>該使用者在此場館下受天花板限制保護之最終有效權限清單。</returns>
    public async Task<IReadOnlyCollection<PermissionGrant>> ForMerchantAsync(
        MerchantMembership membership, Merchant merchant, CancellationToken ct = default)
    {
        // 1. 取得該成員資格角色原始授予的權限清單
        var grants = membership.RoleId is int roleId ? await ForRolesAsync([roleId], ct) : [];
        // 2. 套用該場館之簡化開關展開
        grants = await ApplyMerchantPermissionConfigurationAsync(
            grants, merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled, ct);

        // 3. 查詢該場館所適用的內建 merchant-admin 管理員角色 ID
        using var conn = await factory.CreateOpenAsync(ct);
        var merchantAdminRoleId = await conn.ExecuteScalarAsync<int?>(
            "SELECT id FROM app_role WHERE scope = 1 AND code = 'merchant-admin' AND merchant_id IS NULL");
        if (merchantAdminRoleId is not int adminRoleId) return [];

        // 4. 計算管理員角色之天花板上限，並同樣套用場館簡化展開
        var ceilingGrants = await ForRolesAsync([adminRoleId], ct);
        ceilingGrants = await ApplyMerchantPermissionConfigurationAsync(
            ceilingGrants, merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled, ct);

        // 5. 套用天花板裁決規則取交集，確保無法超越管理員上限
        return MerchantRolePermissionCeilingRules.RestrictGrants(grants, ceilingGrants);
    }

    /// <summary>
    /// 依據場館之簡化開關配置，將權限清單動態展開為完整 CRUD 或所有子功能。
    /// 資料庫仍完整保留細項設定，僅在計算有效 Grant 時展開。
    /// </summary>
    /// <param name="grants">原始權限授予清單。</param>
    /// <param name="isCrudEnabled">是否啟用細部 CRUD 勾選模式。</param>
    /// <param name="isOptionEnabled">是否啟用細部附加子功能勾選模式。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>經簡化規則動態展開後的權限集合。</returns>
    private async Task<IReadOnlyCollection<PermissionGrant>> ApplyMerchantPermissionConfigurationAsync(
        IReadOnlyCollection<PermissionGrant> grants, bool isCrudEnabled, bool isOptionEnabled, CancellationToken ct)
    {
        if (grants.Count == 0 || (isCrudEnabled && isOptionEnabled)) return grants;

        var optionsByCode = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        // 若關閉子選項配置，自資料庫查詢各資源定義之全部子功能清單以供全數展開
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
    /// 依據指定的角色 ID 集合，計算合併後的有效權限清單。
    /// 若包含 FullAccess 角色（如 platform-admin），自動即時擴展為該 Scope 的全部權限與全部子功能。
    /// </summary>
    /// <param name="roleIds">角色主鍵 ID 集合。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>合併去重後之有效權限清單。</returns>
    public async Task<IReadOnlyCollection<PermissionGrant>> ForRolesAsync(IReadOnlyCollection<int> roleIds, CancellationToken ct = default)
    {
        if (roleIds.Count == 0) return [];

        using var conn = await factory.CreateOpenAsync(ct);
        var roleRows = (await conn.QueryAsync<RoleScopeRow>(
            "SELECT id AS Id, scope AS Scope, is_full_access AS IsFullAccess FROM app_role WHERE id IN @roleIds",
            new { roleIds })).ToList();

        // 區分出完全存取之 Scope 與一般自訂角色
        var fullAccessScopes = roleRows.Where(r => r.IsFullAccess).Select(r => r.Scope).Distinct().ToArray();
        var normalRoleIds = roleRows.Where(r => !r.IsFullAccess).Select(r => r.Id).ToArray();

        var grantsByCode = new Dictionary<string, (HashSet<string> Actions, HashSet<string> Options)>(StringComparer.OrdinalIgnoreCase);

        // 1. 若包含完全存取角色，動態讀取目錄下全部資源並賦予所有 CRUD 與子選項
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

        // 2. 處理一般角色之實體關聯表設定
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

    /// <summary>
    /// 字典安全取得或新增項目輔助方法。
    /// </summary>
    /// <param name="map">權限動作與選項快取字典。</param>
    /// <param name="code">權限資源代碼。</param>
    /// <returns>該資源代碼對應的 Actions 與 Options 雜湊集合組合。</returns>
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

    /// <summary>
    /// 安全解析 SubFeaturesJson 陣列字串為子功能識別代碼清單。
    /// </summary>
    /// <param name="json">子功能定義之 JSON 字串。</param>
    /// <returns>子功能代碼字串清單；解析失敗時回傳空集合 (Fail-closed)。</returns>
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

    /// <summary>Dapper 內部查詢角色權限專用 Row 資料物件。</summary>
    private sealed class RolePermissionRow
    {
        /// <summary>權限資源代碼。</summary>
        public required string Code { get; init; }
        /// <summary>是否具有建立權限。</summary>
        public bool PerCreate { get; init; }
        /// <summary>是否具有讀取權限。</summary>
        public bool PerRead { get; init; }
        /// <summary>是否具有修改權限。</summary>
        public bool PerUpdate { get; init; }
        /// <summary>是否具有刪除權限。</summary>
        public bool PerDelete { get; init; }
        /// <summary>已勾選子選項之 JSON 字串。</summary>
        public string? OptionsJson { get; init; }
    }

    /// <summary>Dapper 內部查詢角色 Scope 專用 Row 資料物件。</summary>
    private sealed class RoleScopeRow
    {
        /// <summary>角色 ID。</summary>
        public int Id { get; init; }
        /// <summary>角色 Scope 列舉整數值。</summary>
        public int Scope { get; init; }
        /// <summary>是否為完全存取角色。</summary>
        public bool IsFullAccess { get; init; }
    }

    /// <summary>Dapper 內部查詢完全存取權限定義專用 Row 資料物件。</summary>
    private sealed class FullAccessPermissionRow
    {
        /// <summary>權限資源代碼。</summary>
        public required string Code { get; init; }
        /// <summary>子功能清單定義 JSON 字串。</summary>
        public string? SubFeaturesJson { get; init; }
    }
}
