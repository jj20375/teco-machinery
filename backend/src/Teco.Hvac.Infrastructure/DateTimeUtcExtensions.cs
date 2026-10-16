namespace Teco.Hvac.Infrastructure;

/// <summary>
/// 時間型別 UTC 轉換擴充方法 (<see cref="DateTimeUtcExtensions"/>)。
/// 
/// 專門提供給「Dapper 先讀取至內部私有 Row 類別的 DateTime 欄位，再手動指派給網域物件之 DateTimeOffset 屬性」的場景使用。
/// 由於此類手動指派屬於 C# 隱含轉型，不會觸發 Dapper 的 TypeHandler，若直接轉型會預設套用主機/容器本機時區而產生 8 小時偏差。
/// 透過此擴充方法在指派前明確標記為 UTC，確保絕對時間點精確一致。
/// </summary>
public static class DateTimeUtcExtensions
{
    /// <summary>
    /// 將未指定時區的 <see cref="DateTime"/> 強制指定為 UTC 並轉換為零偏移量的 <see cref="DateTimeOffset"/>。
    /// </summary>
    /// <param name="value">資料庫中讀出的無時區 DateTime 物件。</param>
    /// <returns>帶有 +00:00 偏移量的標準 UTC DateTimeOffset 物件。</returns>
    public static DateTimeOffset AsUtcOffset(this DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>
    /// 將可為空的無時區 <see cref="DateTime"/> 強制指定為 UTC 並轉換為可為空的 <see cref="DateTimeOffset"/>。
    /// </summary>
    /// <param name="value">資料庫中讀出的可為空 DateTime 物件。</param>
    /// <returns>若輸入為 null 則回傳 null；否則回傳帶有 +00:00 偏移量的標準 UTC DateTimeOffset 物件。</returns>
    public static DateTimeOffset? AsUtcOffset(this DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}
