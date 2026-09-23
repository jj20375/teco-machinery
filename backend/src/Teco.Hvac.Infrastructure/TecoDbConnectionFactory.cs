using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Teco.Hvac.Infrastructure;

/// <summary>
/// 集中管理 MySqlConnection 的建立。每次呼叫回傳一條新連線（MySqlConnector 內建連線池），
/// 呼叫端用 using 包起來即可，不需要自行管理生命週期共用。
/// </summary>
public sealed class TecoDbConnectionFactory(IOptions<TecoDbOptions> options)
{
    /// <summary>
    /// 靜態建構子只會跑一次，在這裡註冊全域 Dapper TypeHandler——修正 DateTime→DateTimeOffset
    /// 自動對應會套用容器系統時區的重大 bug，理由見 UtcDateTimeOffsetHandler 上的完整說明。
    /// </summary>
    static TecoDbConnectionFactory()
    {
        SqlMapper.AddTypeHandler(new UtcDateTimeOffsetHandler());
    }

    private readonly string _connectionString = options.Value.ConnectionString;

    public MySqlConnection Create() => new(_connectionString);

    public async Task<MySqlConnection> CreateOpenAsync(CancellationToken ct = default)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
