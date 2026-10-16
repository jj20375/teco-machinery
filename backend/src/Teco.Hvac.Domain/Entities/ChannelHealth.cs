using Teco.Hvac.Contracts;

namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 通道健康狀態歷史紀錄實體 (<see cref="ChannelHealth"/>)。
/// 對應資料庫 channel_health 資料表。
/// 
/// 供應商 DLL 未提供整體健康度介面，由 Collector 服務內之 ChannelWatchdog 追蹤判定，
/// 僅在通訊進入全新區段（斷線或恢復連線）時記錄一筆，用以精確分析各通道通訊可用率與中斷歷時。
/// </summary>
public sealed class ChannelHealth
{
    /// <summary>健康狀態歷史紀錄主鍵識別碼 (Primary Key)。</summary>
    public long Id { get; set; }

    /// <summary>所屬之實體通訊通道（漢鐘閘道、DDC1 或 DDC2）。</summary>
    public required Channel Channel { get; set; }

    /// <summary>該區段記錄開始時之通道連線狀態。</summary>
    public required ConnectionState ConnectionState { get; set; }

    /// <summary>該區段記錄開始時之資料讀取狀態。</summary>
    public required ReadStatus ReadStatus { get; set; }

    /// <summary>狀態變更發生的標準 UTC 時間戳記。</summary>
    public required DateTimeOffset ChangedAtUtc { get; set; }

    /// <summary>該狀態持續的時間長度（單位：秒）；區段尚未結束時為 null。</summary>
    public int? DurationSeconds { get; set; }
}
