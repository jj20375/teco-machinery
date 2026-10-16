using System.Data;
using Dapper;

namespace Teco.Hvac.Infrastructure;

/// <summary>
/// 全域 Dapper DateTimeOffset 型別處理器 (<see cref="UtcDateTimeOffsetHandler"/>)。
/// 
/// 【重大時區修復】
/// 資料庫內部 DATETIME 欄位儲存的數值均為 UTC 標準時間；
/// 然而 MySqlConnector 讀取出來的物件其 Kind 屬性為 <see cref="DateTimeKind.Unspecified"/>。
/// 若未註冊此自訂 TypeHandler，Dapper 預設會呼叫 C# 內建轉型，這會強制套用目前伺服器作業系統之本機時區（在容器中為 Asia/Taipei，UTC+8），
/// 導致反序列化出的 DateTimeOffset 被誤加上 +08:00 偏移量，使得轉換為 UTC 時反向倒退 8 小時。
/// 
/// 透過註冊此 TypeHandler：
/// 1. 讀取 (Parse)：明確將讀出的數值標註為 <see cref="DateTimeKind.Utc"/> 後構建 DateTimeOffset，保證 Offset 一律為 +00:00。
/// 2. 寫入 (SetValue)：將傳入的 DateTimeOffset 轉為 UtcDateTime 寫入資料庫參數，確保儲存純淨的 UTC 時間。
/// </summary>
public sealed class UtcDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    /// <summary>
    /// 將自資料庫查詢所得之原始物件解析為 UTC 標準時區之 <see cref="DateTimeOffset"/>。
    /// </summary>
    /// <param name="value">資料庫查詢回傳的原始物件（通常為 DateTime）。</param>
    /// <returns>以 UTC 時區解讀的 <see cref="DateTimeOffset"/> 實例。</returns>
    public override DateTimeOffset Parse(object value) =>
        new(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));

    /// <summary>
    /// 將 C# 傳入之 <see cref="DateTimeOffset"/> 參數轉換為適合寫入資料庫 DATETIME 欄位的格式。
    /// </summary>
    /// <param name="parameter">SQL 指令參數物件。</param>
    /// <param name="value">欲寫入的 <see cref="DateTimeOffset"/> 數值。</param>
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
        parameter.Value = value.UtcDateTime;
}
