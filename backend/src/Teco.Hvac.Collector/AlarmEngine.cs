using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Alarms;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Collector;

/// <summary>
/// 告警評估：漢鐘走硬體 14 個 IsXxx 旗標（供應商 SDK 直接算好的異常判定，不可設定）
/// ＋可設定門檻（AlarmRule，供水/回水/溫差/累積運轉時數）；FCU 只有可設定門檻（絕對室溫）。
///
/// 兩條關鍵規則（計畫 §2.8、§P5 明確要求）：
/// 1) 只有 ReadStatus == Success（這輪讀取真的成功）才評估／關閉告警。讀取失敗或斷線時
///    兩件事都不做：不開新告警（避免 Gateway 斷線時兩台漢鐘的 14 個旗標一起噴出誤報），
///    也不關閉既有告警（斷線不代表問題解決了，要等到收到一次乾淨的讀取才能結案）。
/// 2) 可設定門檻要 debounce：連續超過門檻達 DebounceSeconds 才真的開告警，避免單次雜訊誤報。
///    規則清單由 CollectorHostedService 定期重新載入（見該檔案的 RunSupervisorLoopAsync），
///    畫面改門檻後不用重建 Collector，約一分鐘內生效。
/// </summary>
public sealed class AlarmEngine(
    AlarmRepository alarmRepository, ChillerMaintenanceRepository maintenanceRepository, ILogger<AlarmEngine> logger)
{
    private static readonly string[] ChillerAlarmFlags =
    [
        nameof(ChillerSnapshot.IsChilledWaterFlowAbnormal),
        nameof(ChillerSnapshot.IsCoolingWaterFlowAbnormal),
        nameof(ChillerSnapshot.IsInverterAbnormal),
        nameof(ChillerSnapshot.IsCompressorOverload),
        nameof(ChillerSnapshot.IsBearingTemperatureTooHigh),
        nameof(ChillerSnapshot.IsDischargeTemperatureTooHigh),
        nameof(ChillerSnapshot.IsMotorTemperatureTooHigh),
        nameof(ChillerSnapshot.IsPowerVoltageTooHigh),
        nameof(ChillerSnapshot.IsPowerVoltageTooLow),
        nameof(ChillerSnapshot.IsCurrentTooHigh),
        nameof(ChillerSnapshot.IsHighPressureTooHigh),
        nameof(ChillerSnapshot.IsLowPressureTooLow),
        nameof(ChillerSnapshot.IsOutletAntiFreezeAbnormal),
        nameof(ChillerSnapshot.IsInletAntiFreezeAbnormal),
    ];

    private IReadOnlyList<AlarmRule> _fcuRules = [];
    private IReadOnlyList<AlarmRule> _chillerRules = [];
    private readonly ConcurrentDictionary<(int RuleId, int DeviceId), DateTimeOffset> _breachSince = new();

    public async Task LoadRulesAsync(CancellationToken ct)
    {
        var rules = await alarmRepository.GetEnabledRulesAsync(ct);
        _fcuRules = rules.Where(r => r.DeviceType == AlarmDeviceType.Fcu).ToList();
        _chillerRules = rules.Where(r => r.DeviceType == AlarmDeviceType.Chiller).ToList();
        logger.LogInformation(
            "告警規則載入完成：FCU 規則 {FcuCount} 條、冰水主機規則 {ChillerCount} 條", _fcuRules.Count, _chillerRules.Count);
    }

    public async Task EvaluateChillerAsync(int deviceId, ChillerSnapshot snapshot, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (snapshot.ReadStatus != ReadStatus.Success) return; // 規則 1：讀取不乾淨時完全不動告警狀態

        foreach (var flagName in ChillerAlarmFlags)
        {
            bool isActive = flagName switch
            {
                nameof(ChillerSnapshot.IsChilledWaterFlowAbnormal) => snapshot.IsChilledWaterFlowAbnormal,
                nameof(ChillerSnapshot.IsCoolingWaterFlowAbnormal) => snapshot.IsCoolingWaterFlowAbnormal,
                nameof(ChillerSnapshot.IsInverterAbnormal) => snapshot.IsInverterAbnormal,
                nameof(ChillerSnapshot.IsCompressorOverload) => snapshot.IsCompressorOverload,
                nameof(ChillerSnapshot.IsBearingTemperatureTooHigh) => snapshot.IsBearingTemperatureTooHigh,
                nameof(ChillerSnapshot.IsDischargeTemperatureTooHigh) => snapshot.IsDischargeTemperatureTooHigh,
                nameof(ChillerSnapshot.IsMotorTemperatureTooHigh) => snapshot.IsMotorTemperatureTooHigh,
                nameof(ChillerSnapshot.IsPowerVoltageTooHigh) => snapshot.IsPowerVoltageTooHigh,
                nameof(ChillerSnapshot.IsPowerVoltageTooLow) => snapshot.IsPowerVoltageTooLow,
                nameof(ChillerSnapshot.IsCurrentTooHigh) => snapshot.IsCurrentTooHigh,
                nameof(ChillerSnapshot.IsHighPressureTooHigh) => snapshot.IsHighPressureTooHigh,
                nameof(ChillerSnapshot.IsLowPressureTooLow) => snapshot.IsLowPressureTooLow,
                nameof(ChillerSnapshot.IsOutletAntiFreezeAbnormal) => snapshot.IsOutletAntiFreezeAbnormal,
                nameof(ChillerSnapshot.IsInletAntiFreezeAbnormal) => snapshot.IsInletAntiFreezeAbnormal,
                _ => false,
            };

            bool wasActive = await alarmRepository.TryFindActiveAsync(AlarmDeviceType.Chiller, deviceId, flagName, ct);

            if (isActive && !wasActive)
            {
                await alarmRepository.OpenEventAsync(
                    AlarmDeviceType.Chiller, deviceId, flagName, AlarmSeverity.Critical, nowUtc, peakValue: 1, ct);
                logger.LogWarning("開啟告警：Chiller#{DeviceId} {Flag}", deviceId, flagName);
            }
            else if (!isActive && wasActive)
            {
                // 逐一關閉：TryFindActiveAsync 只回傳布林，關閉需要事件 id——用 ListAsync 找出來關掉。
                var actives = await alarmRepository.ListAsync(activeOnly: true, limit: 1000, ct: ct);
                var toClose = actives.FirstOrDefault(a =>
                    a.DeviceType == AlarmDeviceType.Chiller && a.DeviceId == deviceId && a.RuleCode == flagName);
                if (toClose is not null)
                {
                    await alarmRepository.CloseEventAsync((int)toClose.Id, nowUtc, ct);
                    logger.LogInformation("關閉告警：Chiller#{DeviceId} {Flag}", deviceId, flagName);
                }
            }
        }

        // 硬體旗標之外，再評估後台「告警門檻設定」畫面可設定的門檻（供水/回水/溫差）。
        // Scope 用 ModbusId 字串比對，"*" 代表全部冰水主機共用。
        // 累積運轉時數不走這裡：通用門檻會在低於門檻時自動關閉告警，保養提醒要等人按「保養完成」才熄燈。
        var applicableRules = _chillerRules
            .Where(r => r.Scope == "*" || r.Scope == snapshot.ModbusId.ToString())
            .ToList();
        await EvaluateThresholdRulesAsync(AlarmDeviceType.Chiller, deviceId,
            applicableRules.Where(r => r.Metric != nameof(ChillerSnapshot.AccumulatedRunningHours)), metric => metric switch
        {
            nameof(ChillerSnapshot.ChilledWaterOutletTemperature) => snapshot.ChilledWaterOutletTemperature,
            nameof(ChillerSnapshot.ChilledWaterInletTemperature) => snapshot.ChilledWaterInletTemperature,
            nameof(ChillerSnapshot.ChilledWaterTemperatureDifference) => snapshot.ChilledWaterTemperatureDifference,
            _ => null,
        }, nowUtc, ct);

        var maintenanceRule = applicableRules.FirstOrDefault(r =>
            r.Metric == nameof(ChillerSnapshot.AccumulatedRunningHours) && r.Operator == AlarmMetricOperator.GreaterThan);
        if (maintenanceRule is not null)
            await EvaluateMaintenanceAsync(deviceId, snapshot.AccumulatedRunningHours, (int)maintenanceRule.Threshold, nowUtc, ct);
    }

    /// <summary>
    /// 保養提醒，比照機車換機油：距上次保養的時數達到間隔就開一筆告警（＝通知一次），
    /// 之後不管拖多久都不會重開，也不會自動關閉，只有 API 的「保養完成」會關掉並重新起算。
    /// 基準點每次都從 DB 讀、不快取：重置是 API 那邊寫的，快取的話重置後下一輪會拿舊基準點又開一次告警。
    /// </summary>
    private async Task EvaluateMaintenanceAsync(int deviceId, int currentHours, int intervalHours, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (intervalHours <= 0) return;

        var state = await maintenanceRepository.GetStateAsync(deviceId, ct);
        if (state.BaselineHours is not { } baseline)
        {
            await maintenanceRepository.InitBaselineIfMissingAsync(deviceId, currentHours, nowUtc, ct);
            logger.LogInformation("保養時數開始計算：Chiller#{DeviceId} 起算點 {Hours}h", deviceId, currentHours);
            return;
        }

        if (currentHours < baseline)
        {
            logger.LogWarning("累積運轉時數倒退（{Current}h < 基準點 {Baseline}h），疑似主機計數器歸零，改以目前時數重新起算：Chiller#{DeviceId}",
                currentHours, baseline, deviceId);
            await maintenanceRepository.RebaseAsync(deviceId, baseline, currentHours, nowUtc, ct);
            return;
        }

        var sinceService = currentHours - (int)baseline;
        if (sinceService < intervalHours) return;

        if (await maintenanceRepository.OpenAlarmIfDueAsync(deviceId, baseline, sinceService, nowUtc, ct))
            logger.LogWarning("開啟保養提醒：Chiller#{DeviceId} 距上次保養 {Since}h（間隔 {Interval}h）", deviceId, sinceService, intervalHours);
    }

    public async Task EvaluateFcuAsync(int deviceId, string floor, FcuSnapshot snapshot, ReadStatus ddcReadStatus,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (ddcReadStatus != ReadStatus.Success) return; // 規則 1：同上

        // 目前只支援室溫（決策見計畫 §2.1：Collector 沒有 FCU 設定溫度，做不出「溫差」規則）。
        var applicableRules = _fcuRules.Where(r => r.Scope == "*" || r.Scope == floor);
        await EvaluateThresholdRulesAsync(AlarmDeviceType.Fcu, deviceId, applicableRules, metric =>
            metric == nameof(FcuSnapshot.Temperature) ? snapshot.Temperature : (double?)null, nowUtc, ct);
    }

    /// <summary>
    /// 可設定門檻規則的共用評估邏輯——冰水主機與 FCU 都走這個，debounce／開關告警的語意完全一致，
    /// 差別只在「這個 metric 名稱要對應到 snapshot 的哪個欄位」，用 resolveMetricValue 這個
    /// callback 解決，resolver 回傳 null 代表這條規則的 metric 目前沒有對應的資料來源，直接跳過。
    /// </summary>
    private async Task EvaluateThresholdRulesAsync(
        AlarmDeviceType deviceType, int deviceId, IEnumerable<AlarmRule> applicableRules,
        Func<string, double?> resolveMetricValue, DateTimeOffset nowUtc, CancellationToken ct)
    {
        foreach (var rule in applicableRules)
        {
            var value = resolveMetricValue(rule.Metric);
            if (value is null) continue;

            var result = AlarmEvaluator.Evaluate(rule, value.Value);
            var key = (rule.Id, deviceId);
            var ruleCode = RuleCode(rule);

            if (result.IsTriggered)
            {
                var breachSince = _breachSince.GetOrAdd(key, nowUtc);
                bool debounceElapsed = (nowUtc - breachSince).TotalSeconds >= rule.DebounceSeconds;
                bool wasActive = await alarmRepository.TryFindActiveAsync(deviceType, deviceId, ruleCode, ct);

                if (debounceElapsed && !wasActive)
                {
                    await alarmRepository.OpenEventAsync(deviceType, deviceId, ruleCode, rule.Severity, nowUtc, result.Value, ct);
                    logger.LogWarning("開啟告警：{DeviceType}#{DeviceId} {Rule} 值={Value}", deviceType, deviceId, ruleCode, result.Value);
                }
            }
            else
            {
                _breachSince.TryRemove(key, out _);
                var actives = await alarmRepository.ListAsync(activeOnly: true, limit: 1000, ct: ct);
                var toClose = actives.FirstOrDefault(a =>
                    a.DeviceType == deviceType && a.DeviceId == deviceId && a.RuleCode == ruleCode);
                if (toClose is not null)
                {
                    await alarmRepository.CloseEventAsync((int)toClose.Id, nowUtc, ct);
                    logger.LogInformation("關閉告警：{DeviceType}#{DeviceId} {Rule}", deviceType, deviceId, ruleCode);
                }
            }
        }
    }

    private static string RuleCode(AlarmRule rule) => rule.RuleCode;
}
