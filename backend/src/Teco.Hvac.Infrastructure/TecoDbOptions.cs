namespace Teco.Hvac.Infrastructure;

/// <summary>
/// 資料庫連線組態選項 (<see cref="TecoDbOptions"/>)。
/// 讀取自 appsettings.json 的 "TecoDb" 區段。
/// </summary>
public sealed class TecoDbOptions
{
    /// <summary>組態設定檔區段名稱。</summary>
    public const string SectionName = "TecoDb";

    /// <summary>MariaDB 資料庫標準連線字串（包含主機、連接埠、資料庫名稱、帳號密碼與連線集區參數）。</summary>
    public required string ConnectionString { get; init; }
}
