using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teco.Hvac.Api.Services;

/// <summary>
/// 全部時序資料在 DB 裡存的都是 UTC（見 CLAUDE.md 的時區原則），但 Dapper/MySqlConnector
/// 從 DATETIME 欄位讀出來的 DateTime.Kind 是 Unspecified，不是 Utc——System.Text.Json 預設
/// 序列化 Unspecified 的 DateTime 時不會加 'Z' 尾碼，前端 `new Date(...)` 遇到沒有時區標記的
/// ISO 字串會當成「瀏覽器所在時區的本地時間」解讀，不是 UTC。這台機器（前台戰情室）跑在
/// Asia/Taipei（UTC+8），結果就是畫面上顯示的所有時間都少算了 8 小時，且完全沒有任何錯誤訊息
/// ——這是一個會讓每個時間欄位都顯示錯誤，卻不會讓任何測試失敗的靜默 bug。
/// 修法：不管 DateTime.Kind 是什麼，序列化時一律當作 UTC 處理、輸出帶 'Z' 的 ISO 字串，
/// 這樣前端不管在哪個時區都能正確解讀。要在 Program.cs 同時掛到 HTTP JSON 選項跟
/// SignalR 的 JSON Hub Protocol，兩邊的序列化設定是分開的，只設一邊還是會漏掉另一邊。
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    /// <summary>
    /// 自 JSON 讀取日期時間，並強制將 Kind 指定為 <see cref="DateTimeKind.Utc"/>。
    /// </summary>
    /// <param name="reader">JSON 讀取器。</param>
    /// <param name="typeToConvert">待轉換型別。</param>
    /// <param name="options">序列化選項。</param>
    /// <returns>帶有 UTC 時區標籤的 <see cref="DateTime"/>。</returns>
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.SpecifyKind(reader.GetDateTime(), DateTimeKind.Utc);

    /// <summary>
    /// 將日期時間序列化為帶有 'Z' 結尾的 ISO-8601 UTC 字串。
    /// </summary>
    /// <param name="writer">JSON 寫入器。</param>
    /// <param name="value">待寫入之日期時間。</param>
    /// <param name="options">序列化選項。</param>
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>同上，給 `DateTime?`（可為 null 的時間欄位，例如告警的 EndedAt）用。</summary>
public sealed class UtcNullableDateTimeConverter : JsonConverter<DateTime?>
{
    /// <summary>
    /// 自 JSON 讀取可為 null 的日期時間，並強制將 Kind 指定為 <see cref="DateTimeKind.Utc"/>。
    /// </summary>
    /// <param name="reader">JSON 讀取器。</param>
    /// <param name="typeToConvert">待轉換型別。</param>
    /// <param name="options">序列化選項。</param>
    /// <returns>帶有 UTC 時區標籤的可空 <see cref="DateTime"/>。</returns>
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : DateTime.SpecifyKind(reader.GetDateTime(), DateTimeKind.Utc);

    /// <summary>
    /// 將可為 null 的日期時間序列化為帶有 'Z' 結尾的 ISO-8601 UTC 字串或 null。
    /// </summary>
    /// <param name="writer">JSON 寫入器。</param>
    /// <param name="value">待寫入之可空日期時間。</param>
    /// <param name="options">序列化選項。</param>
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    }
}
