using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

public sealed class MerchantRepository(TecoDbConnectionFactory factory)
{
    public async Task<IReadOnlyList<Merchant>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var rows = await conn.QueryAsync<MerchantRow>(
            """
            SELECT id, code, name, status,
                   is_role_crud_configuration_enabled AS IsRoleCrudConfigurationEnabled,
                   is_role_option_configuration_enabled AS IsRoleOptionConfigurationEnabled,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM merchant ORDER BY id
            """);
        return rows.Select(ToEntity).ToList();
    }

    public async Task<Merchant?> FindByIdAsync(int id, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MerchantRow>(
            """
            SELECT id, code, name, status,
                   is_role_crud_configuration_enabled AS IsRoleCrudConfigurationEnabled,
                   is_role_option_configuration_enabled AS IsRoleOptionConfigurationEnabled,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM merchant WHERE id = @id
            """, new { id });
        return row is null ? null : ToEntity(row);
    }

    public async Task<Merchant?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MerchantRow>(
            """
            SELECT id, code, name, status,
                   is_role_crud_configuration_enabled AS IsRoleCrudConfigurationEnabled,
                   is_role_option_configuration_enabled AS IsRoleOptionConfigurationEnabled,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM merchant WHERE code = @code
            """, new { code });
        return row is null ? null : ToEntity(row);
    }

    public async Task<int> CreateAsync(Merchant merchant, CancellationToken ct = default)
    {
        using var conn = await factory.CreateOpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO merchant (code, name, status, is_role_crud_configuration_enabled, is_role_option_configuration_enabled)
            VALUES (@Code, @Name, @Status, @IsRoleCrudConfigurationEnabled, @IsRoleOptionConfigurationEnabled);
            SELECT LAST_INSERT_ID();
            """, merchant);
        return (int)id;
    }

    /// <summary>
    /// 更新場館的 CRUD/子項簡化開關（只有平台管理員可呼叫，見 Api 的 PlatformEndpoints）。
    /// 回傳是否真的有變更，供呼叫端決定要不要遞增全場館成員的 AuthVersion。
    /// </summary>
    public async Task<bool> UpdateRolePermissionConfigurationAsync(
        int merchantId, bool? isRoleCrudConfigurationEnabled, bool? isRoleOptionConfigurationEnabled, CancellationToken ct = default)
    {
        var merchant = await FindByIdAsync(merchantId, ct);
        if (merchant is null) return false;

        bool changed = false;
        if (isRoleCrudConfigurationEnabled is bool crud && merchant.IsRoleCrudConfigurationEnabled != crud)
        {
            merchant.IsRoleCrudConfigurationEnabled = crud;
            changed = true;
        }
        if (isRoleOptionConfigurationEnabled is bool option && merchant.IsRoleOptionConfigurationEnabled != option)
        {
            merchant.IsRoleOptionConfigurationEnabled = option;
            changed = true;
        }
        if (!changed) return false;

        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(
            """
            UPDATE merchant SET is_role_crud_configuration_enabled = @IsRoleCrudConfigurationEnabled,
                                 is_role_option_configuration_enabled = @IsRoleOptionConfigurationEnabled,
                                 updated_at = CURRENT_TIMESTAMP(3)
            WHERE id = @Id
            """, new { merchant.Id, merchant.IsRoleCrudConfigurationEnabled, merchant.IsRoleOptionConfigurationEnabled });
        return true;
    }

    private static Merchant ToEntity(MerchantRow r) => new()
    {
        Id = r.Id, Code = r.Code, Name = r.Name, Status = r.Status,
        IsRoleCrudConfigurationEnabled = r.IsRoleCrudConfigurationEnabled,
        IsRoleOptionConfigurationEnabled = r.IsRoleOptionConfigurationEnabled,
        CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt,
    };

    private sealed class MerchantRow
    {
        public int Id { get; init; }
        public required string Code { get; init; }
        public required string Name { get; init; }
        public required string Status { get; init; }
        public bool IsRoleCrudConfigurationEnabled { get; init; }
        public bool IsRoleOptionConfigurationEnabled { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }
}
