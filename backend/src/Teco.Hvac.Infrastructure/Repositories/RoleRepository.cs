using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 角色權限關聯詳細資訊物件 (<see cref="RolePermissionDetail"/>)。
/// 採用類別屬性配合 init 設定子，支援 Dapper 查詢與 JSON 序列化。
/// </summary>
public sealed class RolePermissionDetail
{
    /// <summary>權限資源代碼（例如 "hvac.chiller"、"hvac.fcu"）。</summary>
    public required string PermissionCode { get; init; }
    /// <summary>是否具有建立 (Create) 權限。</summary>
    public bool PerCreate { get; init; }
    /// <summary>是否具有讀取 (Read) 權限。</summary>
    public bool PerRead { get; init; }
    /// <summary>是否具有更新 (Update) 權限。</summary>
    public bool PerUpdate { get; init; }
    /// <summary>是否具有刪除 (Delete) 權限。</summary>
    public bool PerDelete { get; init; }
    /// <summary>已勾選子選項清單之 JSON 字串。</summary>
    public required string OptionsJson { get; init; }
}

/// <summary>
/// 角色與角色權限資料庫倉儲 (<see cref="RoleRepository"/>)。
/// 
/// 負責 app_role 與 app_role_permission 資料表之 CRUD 操作。
/// 支援角色清單篩選、代碼衝突檢驗、角色更名與刪除、細部權限配置查詢與更新。
/// </summary>
public sealed class RoleRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 查詢一般可供管理員挑選之角色清單（排除內部為成員自訂權限建立之 "member-*" 虛擬角色）。
    /// 可選依據 Scope 與 MerchantId 進行範圍過濾。
    /// </summary>
    /// <param name="scope">角色範圍（Platform 或 Merchant）；若為 null 則不限範圍。</param>
    /// <param name="merchantId">所屬場館 ID；若填寫則回傳該場館自訂角色與全域系統範本角色。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>角色實體物件清單。</returns>
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

    /// <summary>
    /// 依據角色主鍵 ID 查詢角色詳細資訊。
    /// </summary>
    /// <param name="id">角色主鍵 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>角色實體物件；若不存在則回傳 null。</returns>
    public async Task<AppRole?> FindByIdAsync(int id, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RoleRow>(
            "SELECT id, code, name, scope AS Scope, merchant_id AS MerchantId, is_system AS IsSystem, " +
            "is_full_access AS IsFullAccess, is_permissions_customized AS IsPermissionsCustomized, created_at AS CreatedAt " +
            "FROM app_role WHERE id = @id", new { id });
        return row is null ? null : ToEntity(row);
    }

    /// <summary>
    /// 依據 Scope、唯一代碼與場館 ID 查詢角色實體。
    /// </summary>
    /// <param name="scope">角色範圍。</param>
    /// <param name="code">角色代碼。</param>
    /// <param name="merchantId">所屬場館 ID（全域角色為 null）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>角色實體物件；若不存在則回傳 null。</returns>
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

    /// <summary>
    /// 檢驗場館自訂角色代碼是否已被同場館或全域系統範本角色占用。
    /// </summary>
    /// <param name="code">欲檢驗的角色代碼。</param>
    /// <param name="merchantId">所屬場館 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>若已被占用回傳 true，可用則回傳 false。</returns>
    public async Task<bool> IsMerchantRoleCodeTakenAsync(string code, int? merchantId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM app_role WHERE scope = @scope AND code = @code AND (merchant_id <=> @merchantId OR merchant_id IS NULL)",
            new { scope = (int)RoleScope.Merchant, code, merchantId });
        return count > 0;
    }

    /// <summary>
    /// 新增一筆角色主檔紀錄。
    /// </summary>
    /// <param name="role">欲建立之角色實體物件。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>新增成功後之自動遞增主鍵 ID。</returns>
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

    /// <summary>
    /// 修改指定自訂角色的中文顯示名稱。
    /// </summary>
    /// <param name="roleId">角色主鍵 ID。</param>
    /// <param name="name">全新中文名稱。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    public async Task RenameAsync(int roleId, string name, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("UPDATE app_role SET name = @name WHERE id = @roleId", new { roleId, name });
    }

    /// <summary>
    /// 刪除指定 ID 之角色（僅限非系統內建角色，is_system = 0 才能被刪除）。
    /// </summary>
    /// <param name="roleId">角色主鍵 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步刪除作業的 <see cref="Task"/>。</returns>
    public async Task DeleteAsync(int roleId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM app_role WHERE id = @roleId AND is_system = 0", new { roleId });
    }

    /// <summary>
    /// 查詢指定角色目前在資料庫中登記的所有細部權限配置明細。
    /// </summary>
    /// <param name="roleId">目標角色 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>該角色被授予的權限與 CRUD 動作清單。</returns>
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

    /// <summary>
    /// 設定或更新指定角色在單一權限資源上的 CRUD 動作與附加子功能。
    /// 若傳入之所有動作皆為 false 且子功能為空，則直接刪除該筆關聯以精簡資料庫列數。
    /// </summary>
    /// <param name="roleId">角色主鍵 ID。</param>
    /// <param name="permissionCode">權限資源代碼。</param>
    /// <param name="perCreate">是否授予建立權限。</param>
    /// <param name="perRead">是否授予讀取權限。</param>
    /// <param name="perUpdate">是否授予修改權限。</param>
    /// <param name="perDelete">是否授予刪除權限。</param>
    /// <param name="options">已勾選附加子功能 Key 集合。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
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

    /// <summary>內部資料映射轉換方法。</summary>
    private static AppRole ToEntity(RoleRow r) => new()
    {
        Id = r.Id, Code = r.Code, Name = r.Name, Scope = (RoleScope)r.Scope, MerchantId = r.MerchantId,
        IsSystem = r.IsSystem, IsFullAccess = r.IsFullAccess, IsPermissionsCustomized = r.IsPermissionsCustomized,
        CreatedAt = r.CreatedAt,
    };

    /// <summary>Dapper 內部查詢專用之 Row 物件。</summary>
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
