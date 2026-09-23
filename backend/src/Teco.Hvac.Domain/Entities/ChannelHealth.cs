using Teco.Hvac.Contracts;

namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 通道健康狀態變化紀錄。DLL 沒有提供「整體健康」API，
/// 由 Collector 的看門狗自行判斷並寫入（計畫 P2 §看門狗）。
/// </summary>
public sealed class ChannelHealth
{
    public long Id { get; set; }
    public required Channel Channel { get; set; }
    public required ConnectionState ConnectionState { get; set; }
    public required ReadStatus ReadStatus { get; set; }
    public required DateTimeOffset ChangedAtUtc { get; set; }
    public int? DurationSeconds { get; set; }
}
