using Dapper;
using Teco.Hvac.Domain.Entities;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 場館 / 租戶商家資料庫倉儲 (<see cref="MerchantRepository"/>)。
/// 
/// 負責 merchant 資料表之存取操作。
/// 支援場館列表查詢、依據 ID 或 Code 單筆查詢、新增場館以及維護場館權限配置簡化模式開關。
/// </summary>
public sealed class MerchantRepository(TecoDbConnectionFactory factory)
{
    /// <summary>
    /// 查詢系統內所有場館主檔清單，依據 ID 升冪排序。
    /// </summary>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>場館實體物件清單。</returns>
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

    /// <summary>
    /// 依據場館主鍵 ID 查詢單一場館資訊。
    /// </summary>
    /// <param name="id">場館主鍵 ID。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>場館實體物件；若不存在則回傳 null。</returns>
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

    /// <summary>
    /// 依據場館唯一代碼 (Code) 查詢單一場館資訊。
    /// </summary>
    /// <param name="code">場館唯一代碼（例如 "teco-golf"）。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>場館實體物件；若不存在則回傳 null。</returns>
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

    /// <summary>
    /// 建立全新的場館主檔紀錄。
    /// </summary>
    /// <param name="merchant">欲建立之場館實體物件。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>建立後之資料庫自動遞增主鍵 ID。</returns>
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
    /// 更新指定場館的角色權限配置簡化模式開關（CRUD 開關與 Option 開關）。
    /// 僅限平台超級管理員執行，修改後若開關有變動回傳 true，供呼叫端觸發該場館所有使用者之 AuthVersion 遞增。
    /// </summary>
    /// <param name="merchantId">目標場館主鍵 ID。</param>
    /// <param name="isRoleCrudConfigurationEnabled">是否啟用細部 CRUD 動作模式；傳入 null 代表不變更。</param>
    /// <param name="isRoleOptionConfigurationEnabled">是否啟用細部附加子選項模式；傳入 null 代表不變更。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>若設定值真正發生變更則回傳 true；若無變更或場館不存在則回傳 false。</returns>
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

    /// <summary>內部資料映射轉換方法。</summary>
    private static Merchant ToEntity(MerchantRow r) => new()
    {
        Id = r.Id, Code = r.Code, Name = r.Name, Status = r.Status,
        IsRoleCrudConfigurationEnabled = r.IsRoleCrudConfigurationEnabled,
        IsRoleOptionConfigurationEnabled = r.IsRoleOptionConfigurationEnabled,
        CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt,
    };

    /// <summary>Dapper 內部查詢專用之 Row 物件。</summary>
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
