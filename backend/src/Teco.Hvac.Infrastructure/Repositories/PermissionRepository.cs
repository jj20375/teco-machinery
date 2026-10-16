using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 權限目錄資料庫倉儲 (<see cref="PermissionRepository"/>)。
/// 
/// 負責 app_permission 資料表之查詢、新增與維護作業。
/// 定義系統受控資源代碼、選單結構、導航路徑與各資源支援之附加子功能。
/// </summary>
public sealed class PermissionRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 查詢系統權限目錄清單。可選依據作用範圍 (Scope) 進行過濾。
    /// </summary>
    /// <param name="scope">目標角色範圍（Platform 或 Merchant）；若為 null 則查詢全部。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>系統權限實體集合。</returns>
    public async Task<IReadOnlyList<AppPermission>> ListAsync(RoleScope? scope = null, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var sql = "SELECT id, code, name, description, scope AS Scope, parent_id AS ParentId, sort_order AS SortOrder, " +
                  "is_menu AS IsMenu, route_path AS RoutePath, sub_features_json AS SubFeaturesJson, created_at AS CreatedAt " +
                  "FROM app_permission WHERE 1=1";
        if (scope is not null) sql += " AND scope = @scope";
        sql += " ORDER BY scope, sort_order, code";

        var rows = await conn.QueryAsync<PermissionRow>(sql, new { scope = (int?)scope });
        return rows.Select(ToEntity).ToList();
    }

    /// <summary>
    /// 依據權限資源唯一代碼查詢權限實體。
    /// </summary>
    /// <param name="code">權限代碼（例如 "hvac.chiller"）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>對應之權限實體；若不存在則回傳 null。</returns>
    public async Task<AppPermission?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<PermissionRow>(
            "SELECT id, code, name, description, scope AS Scope, parent_id AS ParentId, sort_order AS SortOrder, " +
            "is_menu AS IsMenu, route_path AS RoutePath, sub_features_json AS SubFeaturesJson, created_at AS CreatedAt " +
            "FROM app_permission WHERE code = @code", new { code });
        return row is null ? null : ToEntity(row);
    }

    /// <summary>
    /// 新增一筆全新的系統權限目錄項目。
    /// </summary>
    /// <param name="permission">欲建立之權限實體物件。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>建立後之資料庫自動遞增主鍵 ID。</returns>
    public async Task<int> CreateAsync(AppPermission permission, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO app_permission (code, name, description, scope, parent_id, sort_order, is_menu, route_path, sub_features_json)
            VALUES (@Code, @Name, @Description, @Scope, @ParentId, @SortOrder, @IsMenu, @RoutePath, @SubFeaturesJson);
            SELECT LAST_INSERT_ID();
            """, new
            {
                permission.Code, permission.Name, permission.Description, Scope = (int)permission.Scope,
                permission.ParentId, permission.SortOrder, permission.IsMenu, permission.RoutePath, permission.SubFeaturesJson,
            });
        return (int)id;
    }

    /// <summary>
    /// 刪除指定 ID 之系統權限目錄項目。
    /// </summary>
    /// <param name="permissionId">權限主鍵 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步刪除作業的 <see cref="Task"/>。</returns>
    public async Task DeleteAsync(int permissionId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM app_permission WHERE id = @permissionId", new { permissionId });
    }

    /// <summary>
    /// 內部資料映射轉換方法：將 Dapper 查詢所得之 Row 物件映射為領域實體物件。
    /// </summary>
    /// <param name="r">資料庫查詢所得之 Row 物件。</param>
    /// <returns>領域模型 <see cref="AppPermission"/> 實體。</returns>
    private static AppPermission ToEntity(PermissionRow r) => new()
    {
        Id = r.Id, Code = r.Code, Name = r.Name, Description = r.Description, Scope = (RoleScope)r.Scope,
        ParentId = r.ParentId, SortOrder = r.SortOrder, IsMenu = r.IsMenu, RoutePath = r.RoutePath,
        SubFeaturesJson = r.SubFeaturesJson, CreatedAt = r.CreatedAt,
    };

    /// <summary>Dapper 內部查詢專用之 Row 物件。</summary>
    private sealed class PermissionRow
    {
        public int Id { get; init; }
        public required string Code { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public int Scope { get; init; }
        public int? ParentId { get; init; }
        public int SortOrder { get; init; }
        public bool IsMenu { get; init; }
        public string? RoutePath { get; init; }
        public required string SubFeaturesJson { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
