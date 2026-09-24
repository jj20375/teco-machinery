using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>class＋init 屬性，不用 positional record——理由見 MembershipRepository.MerchantUserRow 上的註解。</summary>
public sealed class RolePermissionDetail
{
    public required string PermissionCode { get; init; }
    public bool PerCreate { get; init; }
    public bool PerRead { get; init; }
    public bool PerUpdate { get; init; }
    public bool PerDelete { get; init; }
    public required string OptionsJson { get; init; }
}

public sealed class RoleRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 一般角色清單（角色管理頁、成員角色下拉選單、平台角色列表用）。
    /// 「編輯成員」六個核取方塊面板會幫每個成員建立一個 code = "member-{membershipId}" 的
    /// 專屬角色（見 MerchantEndpoints.SetUserFeatures），這種角色是內部實作細節、不是給人挑選
    /// 的一般角色，一律排除，否則多個成員都存過這個面板後，角色清單會混進好幾筆同樣叫
    /// 「自訂權限」但 id 不同的項目。真的需要解析某個成員目前的專屬角色名稱時，直接讀
    /// membership 列表 JOIN 出來的 RoleName（MembershipRepository.ListAsync），不要走這裡。
    /// </summary>
    public async Task<IReadOnlyList<AppRole>> ListAsync(RoleScope? scope = null, int? merchantId = null, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var sql = "SELECT id, code, name, scope AS Scope, merchant_id AS MerchantId, is_system AS IsSystem, " +
                  "is_full_access AS IsFullAccess, is_permissions_customized AS IsPermissionsCustomized, created_at AS CreatedAt " +
                  "FROM app_role WHERE code NOT LIKE 'member-%'";
        if (scope is not null) sql += " AND scope = @scope";
        if (merchantId is not null) sql += " AND (merchant_id = @merchantId OR merchant_id IS NULL)";
        sql += " ORDER BY scope, is_system DESC, code";

        var rows = await conn.QueryAsync<RoleRow>(sql, new { scope = (int?)scope, merchantId });
        return rows.Select(ToEntity).ToList();
    }

    public async Task<AppRole?> FindByIdAsync(int id, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RoleRow>(
            "SELECT id, code, name, scope AS Scope, merchant_id AS MerchantId, is_system AS IsSystem, " +
            "is_full_access AS IsFullAccess, is_permissions_customized AS IsPermissionsCustomized, created_at AS CreatedAt " +
            "FROM app_role WHERE id = @id", new { id });
        return row is null ? null : ToEntity(row);
    }

    public async Task<AppRole?> FindByCodeAsync(RoleScope scope, string code, int? merchantId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RoleRow>(
            """
            SELECT id, code, name, scope AS Scope, merchant_id AS MerchantId, is_system AS IsSystem,
                   is_full_access AS IsFullAccess, is_permissions_customized AS IsPermissionsCustomized, created_at AS CreatedAt
            FROM app_role WHERE scope = @scope AND code = @code AND merchant_id <=> @merchantId
            """, new { scope = (int)scope, code, merchantId });
        return row is null ? null : ToEntity(row);
    }

    public async Task<int> CreateAsync(AppRole role, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO app_role (code, name, scope, merchant_id, is_system, is_full_access, is_permissions_customized)
            VALUES (@Code, @Name, @Scope, @MerchantId, @IsSystem, @IsFullAccess, @IsPermissionsCustomized);
            SELECT LAST_INSERT_ID();
            """, new
            {
                role.Code, role.Name, Scope = (int)role.Scope, role.MerchantId,
                role.IsSystem, role.IsFullAccess, role.IsPermissionsCustomized,
            });
        return (int)id;
    }

    public async Task RenameAsync(int roleId, string name, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE app_role SET name = @name WHERE id = @roleId", new { roleId, name });
    }

    public async Task DeleteAsync(int roleId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM app_role WHERE id = @roleId AND is_system = 0", new { roleId });
    }

    public async Task<IReadOnlyList<RolePermissionDetail>> GetRolePermissionsAsync(int roleId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<RolePermissionDetail>(
            """
            SELECT p.code AS PermissionCode, rp.per_create AS PerCreate, rp.per_read AS PerRead,
                   rp.per_update AS PerUpdate, rp.per_delete AS PerDelete, rp.options_json AS OptionsJson
            FROM app_role_permission rp
            JOIN app_permission p ON p.id = rp.permission_id
            WHERE rp.role_id = @roleId
            """, new { roleId });
        return rows.ToList();
    }

    /// <summary>Upsert 單一資源的 CRUD/子功能授予；全部動作與子功能皆為空時直接移除該列。</summary>
    public async Task SetRolePermissionAsync(
        int roleId, string permissionCode, bool perCreate, bool perRead, bool perUpdate, bool perDelete,
        IReadOnlyCollection<string> options, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var permissionId = await conn.ExecuteScalarAsync<int?>(
            "SELECT id FROM app_permission WHERE code = @permissionCode", new { permissionCode });
        if (permissionId is null) return;

        if (!perCreate && !perRead && !perUpdate && !perDelete && options.Count == 0)
        {
            await conn.ExecuteAsync(
                "DELETE FROM app_role_permission WHERE role_id = @roleId AND permission_id = @permissionId",
                new { roleId, permissionId });
            return;
        }

        var optionsJson = System.Text.Json.JsonSerializer.Serialize(options);
        await conn.ExecuteAsync(
            """
            INSERT INTO app_role_permission (role_id, permission_id, per_create, per_read, per_update, per_delete, options_json)
            VALUES (@roleId, @permissionId, @perCreate, @perRead, @perUpdate, @perDelete, @optionsJson)
            ON DUPLICATE KEY UPDATE
                per_create = @perCreate, per_read = @perRead, per_update = @perUpdate, per_delete = @perDelete,
                options_json = @optionsJson
            """, new { roleId, permissionId, perCreate, perRead, perUpdate, perDelete, optionsJson });
    }

    private static AppRole ToEntity(RoleRow r) => new()
    {
        Id = r.Id, Code = r.Code, Name = r.Name, Scope = (RoleScope)r.Scope, MerchantId = r.MerchantId,
        IsSystem = r.IsSystem, IsFullAccess = r.IsFullAccess, IsPermissionsCustomized = r.IsPermissionsCustomized,
        CreatedAt = r.CreatedAt,
    };

    private sealed class RoleRow
    {
        public int Id { get; init; }
        public required string Code { get; init; }
        public required string Name { get; init; }
        public int Scope { get; init; }
        public int? MerchantId { get; init; }
        public bool IsSystem { get; init; }
        public bool IsFullAccess { get; init; }
        public bool IsPermissionsCustomized { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
