using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
///
/// 【溫度門檻只在設備運轉中判定】（2026-10-16，決策見 docs/BACKEND_INTEGRATION_PLAN.md「告警只在運轉中判定」）
/// 冰水主機停機時水溫會回升到環境溫度、溫差趨近 0；FCU 關機的區域室溫自然偏高——這些都不是設備異常。
/// 停機／關機時不評估溫度門檻，且把已開著的溫度門檻告警結案；冰水主機重新開機後另有暖機寬限。
/// 14 個硬體旗標、保養提醒、通道離線不受影響，停機時照常判定。
/// </summary>
public sealed class AlarmEngine(
    AlarmRepository alarmRepository, ChillerMaintenanceRepository maintenanceRepository,
    IOptions<CollectorOptions> options, ILogger<AlarmEngine> logger)
{
    private readonly TimeSpan _chillerWarmup = TimeSpan.FromMinutes(Math.Max(0, options.Value.ChillerWarmupMinutes));

    /// <summary>
    /// 冰水主機這一段運轉從何時起算暖機；null＝上一輪看到的是停止，沒有鍵＝Collector 啟動後還沒看過這台。
    /// </summary>
    private readonly ConcurrentDictionary<int, DateTimeOffset?> _chillerRunningSince = new();

    /// <summary>FCU 上一輪是否開機；只用來在「開→關」那一輪結案一次，不必每輪對每台關機的 FCU 下 UPDATE。</summary>
    private readonly ConcurrentDictionary<int, bool> _fcuSwitchedOn = new();

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

    private string _rulesFingerprint = "";
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    /// <summary>
    /// 每次評估前呼叫：規則在後台被改過就立刻重載。後台改的門檻不能讓使用者等一段「快取過期」的時間才生效，
    /// 不然畫面上設定值和實際判定會對不上（改了範圍卻還在用舊門檻告警）。
    /// 比對失敗（資料庫暫時連不上）時沿用目前規則，下一輪再試。
    /// </summary>
    public async Task RefreshRulesIfChangedAsync(CancellationToken ct)
    {
        if (!await _reloadLock.WaitAsync(0, ct)) return;
        try
        {
            if (await alarmRepository.GetRulesFingerprintAsync(ct) != _rulesFingerprint)
                await LoadRulesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "檢查告警規則是否異動失敗，沿用目前規則直到下次評估");
        }
        finally { _reloadLock.Release(); }
    }

    public async Task LoadRulesAsync(CancellationToken ct)
    {
        // 指紋要在讀規則「之前」取：讀的途中若又被改，下一輪指紋不同會再重載一次。
        _rulesFingerprint = await alarmRepository.GetRulesFingerprintAsync(ct);
        var rules = await alarmRepository.GetEnabledRulesAsync(ct);
        _fcuRules = rules.Where(r => r.DeviceType == AlarmDeviceType.Fcu).ToList();
        _chillerRules = rules.Where(r => r.DeviceType == AlarmDeviceType.Chiller).ToList();
        logger.LogInformation(
            "告警規則載入完成：FCU 規則 {FcuCount} 條、冰水主機規則 {ChillerCount} 條", _fcuRules.Count, _chillerRules.Count);

        // 門檻改過之後，舊代碼的告警沒人會再評估也沒人會關；API 改門檻當下會關一次，但那一刻 Collector
        // 還握著舊規則，可能又開回來，所以每次換上新規則後掃一次。
        var closed = await alarmRepository.CloseOrphanThresholdEventsAsync(
            rules.Select(r => r.RuleCode).ToList(), DateTimeOffset.UtcNow, ct);
        if (closed > 0)
            logger.LogInformation("已關閉 {Count} 筆門檻已變更、找不到對應規則的告警", closed);
    }

    /// <summary>通道離線告警的 rule_code，沒有門檻也不走 alarm_rule，跟 14 個硬體旗標一樣由引擎直接判斷。</summary>
    public const string ChannelOfflineRuleCode = "ChannelOffline";

    /// <summary>連續讀不到這麼久才開告警：DLL 偶爾會單輪失敗或重連，不能一閃就通知。</summary>
    private static readonly TimeSpan ChannelOfflineDebounce = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<Channel, DateTimeOffset> _offlineSince = new();

    /// <summary>
    /// 通道離線告警（離線也要通知）：連續 60 秒沒有成功讀取就開一筆，恢復成功讀取就結案。
    /// 一條通道一筆告警，不逐台設備開——DDC 讀取失敗時整層 FCU（64／31 台）都讀不到，逐台會一次冒出幾十筆。
    /// 「失敗」包含連線中斷與某一站故障（供應商 DLL 一站失敗整台 DDC 判為失敗），兩者從這裡分不出來，
    /// 差別看平台「系統診斷」頁。NotRead（還沒讀、剛啟動或重連中）不算失敗也不算恢復，狀態維持原樣。
    /// 告警存在資料庫，Collector 重啟後仍然有效：重啟後計時重來，但已經開著的不會重複開。
    /// </summary>
    public async Task EvaluateChannelAsync(Channel channel, ReadStatus readStatus, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var deviceId = (int)channel;
        switch (readStatus)
        {
            case ReadStatus.NotRead:
                return;
            case ReadStatus.Success:
                _offlineSince.TryRemove(channel, out _);
                if (await alarmRepository.CloseActiveEventsAsync(AlarmDeviceType.Channel, deviceId, ChannelOfflineRuleCode, nowUtc, ct) > 0)
                    logger.LogInformation("通道恢復，關閉離線告警：{Channel}", channel);
                return;
            default:
                var since = _offlineSince.GetOrAdd(channel, nowUtc);
                if (nowUtc - since < ChannelOfflineDebounce) return;
                if (await alarmRepository.TryFindActiveAsync(AlarmDeviceType.Channel, deviceId, ChannelOfflineRuleCode, ct)) return;
                await alarmRepository.OpenEventAsync(AlarmDeviceType.Channel, deviceId, ChannelOfflineRuleCode,
                    AlarmSeverity.Critical, nowUtc, peakValue: (double)readStatus, ct);
                logger.LogWarning("開啟告警：通道離線 {Channel}（讀取狀態 {Status}，已連續 {Seconds} 秒讀不到）",
                    channel, readStatus, (int)(nowUtc - since).TotalSeconds);
                return;
        }
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
        var temperatureRules = applicableRules
            .Where(r => r.Metric != nameof(ChillerSnapshot.AccumulatedRunningHours))
            .ToList();
        if (await IsChillerReadyForTemperatureRulesAsync(deviceId, snapshot, temperatureRules, nowUtc, ct))
        {
            await EvaluateThresholdRulesAsync(AlarmDeviceType.Chiller, deviceId, temperatureRules, metric => metric switch
            {
                nameof(ChillerSnapshot.ChilledWaterOutletTemperature) => snapshot.ChilledWaterOutletTemperature,
                nameof(ChillerSnapshot.ChilledWaterInletTemperature) => snapshot.ChilledWaterInletTemperature,
                nameof(ChillerSnapshot.ChilledWaterTemperatureDifference) => snapshot.ChilledWaterTemperatureDifference,
                _ => null,
            }, nowUtc, ct);
        }

        var maintenanceRule = applicableRules.FirstOrDefault(r =>
            r.Metric == nameof(ChillerSnapshot.AccumulatedRunningHours) && r.Operator == AlarmMetricOperator.GreaterThan);
        if (maintenanceRule is not null)
            await EvaluateMaintenanceAsync(deviceId, snapshot.AccumulatedRunningHours, (int)maintenanceRule.Threshold, nowUtc, ct);
    }

    /// <summary>
    /// 冰水主機是否在運轉：沿用前端 hvac-service.ts 的 deriveChillerStatus（負載率 &gt; 0 為運轉中），
    /// 兩邊要一致，否則畫面顯示「停止」的主機卻開出溫度告警。
    /// </summary>
    /// <param name="snapshot">冰水主機最新快照。</param>
    /// <returns>運轉中回傳 true。</returns>
    public static bool IsChillerRunning(ChillerSnapshot snapshot) => snapshot.LoadPercentage > 0;

    /// <summary>
    /// 依運轉狀態決定這一輪要不要評估冰水主機的溫度門檻，並處理停機結案與開機暖機寬限。
    /// </summary>
    /// <param name="deviceId">主機在資料庫中的主鍵 ID。</param>
    /// <param name="snapshot">冰水主機最新快照（已確認讀取成功）。</param>
    /// <param name="temperatureRules">適用於這台主機的溫度類門檻規則。</param>
    /// <param name="nowUtc">目前計算使用的標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>運轉中且暖機寬限已過時回傳 true。</returns>
    private async Task<bool> IsChillerReadyForTemperatureRulesAsync(
        int deviceId, ChillerSnapshot snapshot, IReadOnlyList<AlarmRule> temperatureRules, DateTimeOffset nowUtc, CancellationToken ct)
    {
        bool seenBefore = _chillerRunningSince.TryGetValue(deviceId, out var runningSince);

        if (!IsChillerRunning(snapshot))
        {
            // Collector 剛啟動時第一次看到停機也要結案一次：可能是上次程序結束前開著、之後主機才停的告警
            if (!seenBefore || runningSince is not null)
            {
                _chillerRunningSince[deviceId] = null;
                var closed = await CloseThresholdAlarmsAsync(AlarmDeviceType.Chiller, deviceId, temperatureRules, nowUtc, ct);
                if (seenBefore) logger.LogInformation("冰水主機停機：Chiller#{DeviceId}，暫停溫度門檻判定", deviceId);
                if (closed > 0) logger.LogInformation("停機結案 {Count} 筆溫度門檻告警：Chiller#{DeviceId}", closed, deviceId);
            }
            return false;
        }

        if (!seenBefore)
        {
            // Collector 啟動時主機已經在運轉，不知道何時開機的，當成已暖機完成。
            // 反過來當成剛開機的話，每次重啟 Collector 都會有一段時間對真的異常視而不見。
            _chillerRunningSince[deviceId] = nowUtc - _chillerWarmup;
            return true;
        }

        if (runningSince is null)
        {
            _chillerRunningSince[deviceId] = nowUtc;
            logger.LogInformation("冰水主機開機：Chiller#{DeviceId}，暖機寬限 {Minutes} 分鐘後才判定溫度門檻",
                deviceId, _chillerWarmup.TotalMinutes);
            return _chillerWarmup <= TimeSpan.Zero;
        }

        return nowUtc - runningSince.Value >= _chillerWarmup;
    }

    /// <summary>
    /// 設備停機／關機時結案它的溫度門檻告警，並清掉防抖計時，下次開機從零開始計算連續超標時間。
    /// </summary>
    /// <param name="deviceType">設備類型。</param>
    /// <param name="deviceId">設備在資料庫中的主鍵 ID。</param>
    /// <param name="rules">要結案的門檻規則。</param>
    /// <param name="nowUtc">結案時間戳記。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>被結案的告警筆數。</returns>
    private async Task<int> CloseThresholdAlarmsAsync(
        AlarmDeviceType deviceType, int deviceId, IReadOnlyList<AlarmRule> rules, DateTimeOffset nowUtc, CancellationToken ct)
    {
        foreach (var rule in rules) _breachSince.TryRemove((rule.Id, deviceId), out _);
        return await alarmRepository.CloseActiveEventsAsync(
            deviceType, deviceId, rules.Select(RuleCode).ToList(), nowUtc, ct);
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
        var applicableRules = _fcuRules.Where(r => r.Scope == "*" || r.Scope == floor).ToList();

        switch (snapshot.SwitchStatus)
        {
            case FcuSwitchStatus.On:
                _fcuSwitchedOn[deviceId] = true;
                break;
            case FcuSwitchStatus.Off:
                // 關機的區域沒有空調，室溫超標是正常現象；只在「開→關」那一輪（或啟動後第一次看到）結案一次
                if (!_fcuSwitchedOn.TryGetValue(deviceId, out var wasOn) || wasOn)
                {
                    _fcuSwitchedOn[deviceId] = false;
                    var closed = await CloseThresholdAlarmsAsync(AlarmDeviceType.Fcu, deviceId, applicableRules, nowUtc, ct);
                    if (closed > 0) logger.LogInformation("FCU 關機結案 {Count} 筆室溫告警：Fcu#{DeviceId}", closed, deviceId);
                }
                return;
            default:
                // 開關狀態未知：跟讀取失敗一樣，不開新告警也不結案既有告警
                return;
        }

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
