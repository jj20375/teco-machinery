namespace Teco.Hvac.Infrastructure;

/// <summary>
/// 給「Dapper 先讀進一個 DateTime 型別的私有 Row 類別欄位，Repository 再手動指派給網域物件的
/// DateTimeOffset 屬性」這種寫法用——這種手動指派是 C# 語言層級的隱含轉換，不會經過
/// UtcDateTimeOffsetHandler（那個只攔截 Dapper 直接對應到 DateTimeOffset 屬性的情況），
/// 一樣會被容器系統時區（TZ=Asia/Taipei）污染，需要在指派前明確標記成 Utc。
/// </summary>
public static class DateTimeUtcExtensions
{
    public static DateTimeOffset AsUtcOffset(this DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? AsUtcOffset(this DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}
