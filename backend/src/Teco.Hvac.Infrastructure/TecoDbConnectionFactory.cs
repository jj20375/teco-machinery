using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Teco.Hvac.Infrastructure;

/// <summary>
/// MariaDB 資料庫連線工廠 (<see cref="TecoDbConnectionFactory"/>)。
/// 
/// 集中管理 <see cref="MySqlConnection"/> 的建立。
/// 每次呼叫回傳一條全新的連線實例（底層由 MySqlConnector 內建高效能連線集區自動複用），
/// 呼叫端僅需使用 using 區塊包裹即可，無需額外手動管理連線生命週期。
/// </summary>
public sealed class TecoDbConnectionFactory(IOptions<TecoDbOptions> options)
{
    /// <summary>
    /// 靜態建構子：確保在應用程式生命週期內僅執行一次。
    /// 於此處註冊全域 Dapper <see cref="UtcDateTimeOffsetHandler"/> 型別處理器，
    /// 徹底解決資料庫 DATETIME 欄位自動對應為 DateTimeOffset 時被容器時區 (Asia/Taipei) 污染造成 8 小時時間偏移的關鍵問題。
    /// </summary>
    static TecoDbConnectionFactory()
    {
        SqlMapper.AddTypeHandler(new UtcDateTimeOffsetHandler());
    }

    /// <summary>資料庫連線字串。</summary>
    private readonly string _connectionString = options.Value.ConnectionString;

    /// <summary>
    /// 建立一條未開啟的全新 <see cref="MySqlConnection"/> 連線實例。
    /// </summary>
    /// <returns>未開啟的 MySQL 資料庫連線物件。</returns>
    public MySqlConnection Create() => new(_connectionString);

    /// <summary>
    /// 建立並非同步開啟一條全新的 <see cref="MySqlConnection"/> 連線實例。
    /// </summary>
    /// <param name="ct">控制非同步連線開啟的取消語彙基元。</param>
    /// <returns>已成功開啟連線的 <see cref="MySqlConnection"/> 物件。</returns>
    public async Task<MySqlConnection> CreateOpenAsync(CancellationToken ct = default)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
