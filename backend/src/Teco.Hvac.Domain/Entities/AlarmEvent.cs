namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 告警事件紀錄實體 (<see cref="AlarmEvent"/>)。
/// 對應資料庫 alarm_event 資料表。
/// 
/// 採用狀態機模式管理生命週期：
/// - 當異常發生且通過防抖動延遲時，新增一筆記錄，EndedAtUtc 為 null。
/// - 當異常排除且收到有效讀值時，更新同一筆記錄之 EndedAtUtc，代表告警解除結案。
/// </summary>
public sealed class AlarmEvent
{
    /// <summary>告警事件主鍵識別碼 (Primary Key)。</summary>
    public long Id { get; set; }

    /// <summary>告警所屬之設備類型（冰水主機、FCU 或實體通訊通道）。</summary>
    public required AlarmDeviceType DeviceType { get; set; }

    /// <summary>發生告警之設備資料庫主鍵 ID（若為通道則為 Channel 列舉數值）。</summary>
    public required int DeviceId { get; set; }

    /// <summary>觸發此告警之規則識別代碼（例如 "ChilledWaterOutletTemperature.GreaterThan.12.5"、"IsCompressorOverload"）。</summary>
    public required string RuleCode { get; set; }

    /// <summary>告警嚴重度等級（Info、Warning 或 Critical）。</summary>
    public required Contracts.AlarmSeverity Severity { get; set; }

    /// <summary>告警觸發開立之標準 UTC 時間戳記。</summary>
    public required DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>告警恢復正常解除之標準 UTC 時間戳記；若告警仍在持續中則為 null。</summary>
    public DateTimeOffset? EndedAtUtc { get; set; }

    /// <summary>觸發期間記錄之最極端物理量數值（例如最高量測溫度或最高電流）。</summary>
    public double? PeakValue { get; set; }

    /// <summary>執行人工確認 (Acknowledge) 之使用者 ID；尚未確認時為 null。</summary>
    public int? AckByUserId { get; set; }

    /// <summary>執行人工確認之標準 UTC 時間戳記；尚未確認時為 null。</summary>
    public DateTimeOffset? AckAtUtc { get; set; }

    /// <summary>值班人員或工程師填寫之處理備註說明。</summary>
    public string? Memo { get; set; }

    /// <summary>取得此告警是否仍處於作用中（未結案）狀態。</summary>
    public bool IsActive => EndedAtUtc is null;
}
