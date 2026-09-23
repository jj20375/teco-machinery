using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

public sealed class PermissionRepository(TecoDbConnectionFactory factory)
{
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

    public async Task<AppPermission?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<PermissionRow>(
            "SELECT id, code, name, description, scope AS Scope, parent_id AS ParentId, sort_order AS SortOrder, " +
            "is_menu AS IsMenu, route_path AS RoutePath, sub_features_json AS SubFeaturesJson, created_at AS CreatedAt " +
            "FROM app_permission WHERE code = @code", new { code });
        return row is null ? null : ToEntity(row);
    }

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

    public async Task DeleteAsync(int permissionId, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM app_permission WHERE id = @permissionId", new { permissionId });
    }

    private static AppPermission ToEntity(PermissionRow r) => new()
    {
        Id = r.Id, Code = r.Code, Name = r.Name, Description = r.Description, Scope = (RoleScope)r.Scope,
        ParentId = r.ParentId, SortOrder = r.SortOrder, IsMenu = r.IsMenu, RoutePath = r.RoutePath,
        SubFeaturesJson = r.SubFeaturesJson, CreatedAt = r.CreatedAt,
    };

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
