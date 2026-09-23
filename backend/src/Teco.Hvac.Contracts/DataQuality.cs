namespace Teco.Hvac.Contracts;

/// <summary>
/// 每一筆對外資料都要附帶的資料品質描述。
/// 因為 Collector 沒有逐欄位時間戳、也沒有 LastSuccessTime，
/// 這個欄位是前端判斷「這個數值還能不能信」的唯一依據。
/// </summary>
public sealed class DataQuality
{
    public required Channel Channel { get; init; }
    public required bool IsConnected { get; init; }
    public required ReadStatus ReadStatus { get; init; }
    public DateTimeOffset? LastSuccessAtUtc { get; init; }
    public double? StaleSeconds { get; init; }
}
