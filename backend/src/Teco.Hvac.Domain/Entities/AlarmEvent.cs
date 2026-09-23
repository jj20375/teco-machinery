namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 告警事件，狀態機式紀錄（進入/離開各一筆，見計畫 P2 §寫入節流）。
/// </summary>
public sealed class AlarmEvent
{
    public long Id { get; set; }
    public required AlarmDeviceType DeviceType { get; set; }
    public required int DeviceId { get; set; }
    public required string RuleCode { get; set; }
    public required Contracts.AlarmSeverity Severity { get; set; }
    public required DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public double? PeakValue { get; set; }
    public int? AckByUserId { get; set; }
    public DateTimeOffset? AckAtUtc { get; set; }
    public string? Memo { get; set; }

    public bool IsActive => EndedAtUtc is null;
}
