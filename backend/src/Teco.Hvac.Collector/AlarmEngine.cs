using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Alarms;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Collector;

/// <summary>
/// 告警評估與開立引擎 (AlarmEngine)。
/// 
/// 負責評估設備遙測數據並管理告警的生命週期（開立、防抖動延遲、解除結案）。
/// 
/// 【評估分類】
/// 1. 漢鐘冰水主機：包含硬體自帶之 14 個異常旗標（供應商 SDK 內建判定，不可設定）
///    ＋ 使用者自訂動態門檻（出水溫度、進水溫度、水溫溫差、累積運轉時數保養提醒）。
/// 2. FCU 送風機：使用者自訂之動態溫度門檻（絕對室溫）。
/// 3. 通訊通道：連續離線判定（超過 60 秒無成功讀取即觸發通道離線告警）。
/// 
/// 【兩大關鍵核心原則】
/// 1. 讀取未成功不異動：只有在 ReadStatus == Success 時才進行設備告警評估與結案。
///    通訊中斷時不開新設備告警（防止因通訊失敗導致 14 個旗標全部誤報），亦不關閉既有告警（問題未證實已排除）。
/// 2. 門檻防抖動 (Debounce)：動態門檻必須連續超標達設定之 DebounceSeconds 秒數才開立告警，過濾單次雜訊。
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

    /// <summary>
    /// 漢鐘冰水主機 14 個硬體故障警報旗標名稱清單（對齊原廠說明書表 14）。
    /// </summary>
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

    /// <summary>目前載入之 FCU 動態告警門檻規則清單快取。</summary>
    private IReadOnlyList<AlarmRule> _fcuRules = [];

    /// <summary>目前載入之冰水主機動態告警門檻規則清單快取。</summary>
    private IReadOnlyList<AlarmRule> _chillerRules = [];

    /// <summary>
    /// 記錄規則違規超標的起始時間字典，鍵值為 (RuleId, DeviceId)。
    /// 用於計算持續超標時間是否達到 DebounceSeconds 門檻。
    /// </summary>
    private readonly ConcurrentDictionary<(int RuleId, int DeviceId), DateTimeOffset> _breachSince = new();

    /// <summary>
    /// 告警規則快取指紋字串（由資料庫規則更新時間與內容產生），用於比對規則是否在後台遭修改。
    /// </summary>
    private string _rulesFingerprint = "";

    /// <summary>重新載入告警規則時使用的非同步號誌鎖，防止多個線程同時進行重載。</summary>
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    /// <summary>
    /// 每次評估前呼叫此方法：比對資料庫告警規則指紋，若後台管理員修改了門檻則立即熱重載。
    /// </summary>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步檢查作業的 <see cref="Task"/>。</returns>
    public async Task RefreshRulesIfChangedAsync(CancellationToken ct)
    {
        // 嘗試取得重載鎖，若已有其他線程正在檢查則直接返回
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

    /// <summary>
    /// 從資料庫重新載入所有已啟用的告警門檻規則，並將已遭刪除或停用規則的遺留告警自動結案。
    /// </summary>
    /// <param name="ct">控制非同步載入作業取消的語彙基元。</param>
    /// <returns>代表非同步載入作業的 <see cref="Task"/>。</returns>
    public async Task LoadRulesAsync(CancellationToken ct)
    {
        // 先讀取資料庫指紋
        _rulesFingerprint = await alarmRepository.GetRulesFingerprintAsync(ct);
        var rules = await alarmRepository.GetEnabledRulesAsync(ct);
        _fcuRules = rules.Where(r => r.DeviceType == AlarmDeviceType.Fcu).ToList();
        _chillerRules = rules.Where(r => r.DeviceType == AlarmDeviceType.Chiller).ToList();
        logger.LogInformation(
            "告警規則載入完成：FCU 規則 {FcuCount} 條、冰水主機規則 {ChillerCount} 條", _fcuRules.Count, _chillerRules.Count);

        // 清理不再存在於啟用清單中的孤兒告警事件
        var closed = await alarmRepository.CloseOrphanThresholdEventsAsync(
            rules.Select(r => r.RuleCode).ToList(), DateTimeOffset.UtcNow, ct);
        if (closed > 0)
            logger.LogInformation("已關閉 {Count} 筆門檻已變更、找不到對應規則的告警", closed);
    }

    /// <summary>通道通訊離線告警的固定規則代碼。</summary>
    public const string ChannelOfflineRuleCode = "ChannelOffline";

    /// <summary>通道離線判定防抖動時間（連續 60 秒讀取失敗或中斷才開立告警）。</summary>
    private static readonly TimeSpan ChannelOfflineDebounce = TimeSpan.FromSeconds(60);

    /// <summary>記錄各通訊通道進入未讀取成功狀態的起始時間戳記字典。</summary>
    private readonly ConcurrentDictionary<Channel, DateTimeOffset> _offlineSince = new();

    /// <summary>
    /// 評估特定實體通訊通道的連線健康度並開立或關閉通道離線告警。
    /// </summary>
    /// <param name="channel">目標通訊通道。</param>
    /// <param name="readStatus">該通道最新回報的讀取狀態。</param>
    /// <param name="nowUtc">目前計算使用的標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步評估作業取消的語彙基元。</param>
    /// <returns>代表非同步評估作業的 <see cref="Task"/>。</returns>
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

    /// <summary>
    /// 評估單台漢鐘冰水主機的 14 個硬體異常旗標、動態水溫門檻以及累積運轉時數保養提醒。
    /// </summary>
    /// <param name="deviceId">主機在資料庫中的主鍵 ID。</param>
    /// <param name="snapshot">採集到的冰水主機最新遙測快照。</param>
    /// <param name="nowUtc">目前計算使用的標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步評估作業取消的語彙基元。</param>
    /// <returns>代表非同步評估作業的 <see cref="Task"/>。</returns>
    public async Task EvaluateChillerAsync(int deviceId, ChillerSnapshot snapshot, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // 【核心原則 1】：讀取狀態不為成功時完全不更動告警狀態
        if (snapshot.ReadStatus != ReadStatus.Success) return;

        // 1. 逐一比對 14 個硬體故障旗標
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

        // 2. 評估可設定的動態門檻規則（出水溫、進水溫、溫差）——只在運轉中且暖機寬限已過時
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

        // 3. 評估主機累積運轉時數保養提醒
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
    /// 評估冰水主機累積運轉時數是否已達保養週期。
    /// 類似車輛里程保養概念，自上次保養基準點時數起算，若運轉時數達到間隔則開啟保養通知告警。
    /// </summary>
    /// <param name="deviceId">主機在資料庫中的主鍵 ID。</param>
    /// <param name="currentHours">目前採集到的累積運轉時數數值。</param>
    /// <param name="intervalHours">保養間隔時數門檻（例如 1000 小時）。</param>
    /// <param name="nowUtc">目前計算使用的標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步評估作業取消的語彙基元。</param>
    /// <returns>代表非同步評估作業的 <see cref="Task"/>。</returns>
    private async Task EvaluateMaintenanceAsync(int deviceId, int currentHours, int intervalHours, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (intervalHours <= 0) return;

        var state = await maintenanceRepository.GetStateAsync(deviceId, ct);
        if (state.BaselineHours is not { } baseline)
        {
            // 首次啟動尚無基準時數，以當前時數作為起始起算點
            await maintenanceRepository.InitBaselineIfMissingAsync(deviceId, currentHours, nowUtc, ct);
            logger.LogInformation("保養時數開始計算：Chiller#{DeviceId} 起算點 {Hours}h", deviceId, currentHours);
            return;
        }

        if (currentHours < baseline)
        {
            // 異常情況：運轉時數倒退（可能主機重設控制器計時器），重新校正基準點
            logger.LogWarning("累積運轉時數倒退（{Current}h < 基準點 {Baseline}h），疑似主機計數器歸零，改以目前時數重新起算：Chiller#{DeviceId}",
                currentHours, baseline, deviceId);
            await maintenanceRepository.RebaseAsync(deviceId, baseline, currentHours, nowUtc, ct);
            return;
        }

        var sinceService = currentHours - (int)baseline;
        if (sinceService < intervalHours) return;

        // 開立保養告警通知
        if (await maintenanceRepository.OpenAlarmIfDueAsync(deviceId, baseline, sinceService, nowUtc, ct))
            logger.LogWarning("開啟保養提醒：Chiller#{DeviceId} 距上次保養 {Since}h（間隔 {Interval}h）", deviceId, sinceService, intervalHours);
    }

    /// <summary>
    /// 評估單台 FCU 設備的動態溫度門檻規則。
    /// </summary>
    /// <param name="deviceId">FCU 在資料庫中的主鍵 ID。</param>
    /// <param name="floor">FCU 所屬樓層代碼（如 "B1" 或 "B2"）。</param>
    /// <param name="snapshot">採集到的 FCU 即時快照。</param>
    /// <param name="ddcReadStatus">所屬 DDC 控制器的讀取狀態。</param>
    /// <param name="nowUtc">目前計算使用的標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步評估作業取消的語彙基元。</param>
    /// <returns>代表非同步評估作業的 <see cref="Task"/>。</returns>
    public async Task EvaluateFcuAsync(int deviceId, string floor, FcuSnapshot snapshot, ReadStatus ddcReadStatus,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (ddcReadStatus != ReadStatus.Success) return;

        // 篩選套用於全區 (*) 或該樓層之 FCU 規則
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
    /// 共用之動態門檻規則評估函式（冰水主機與 FCU 共用此邏輯）。
    /// 負責計算防抖動時間、比對觸發條件並調用 Repository 執行告警開立或結案。
    /// </summary>
    /// <param name="deviceType">設備類型（Chiller、Fcu 或 Channel）。</param>
    /// <param name="deviceId">設備在資料庫中的主鍵 ID。</param>
    /// <param name="applicableRules">適用於此設備的規則集合。</param>
    /// <param name="resolveMetricValue">用於依據指標名稱獲取目前數值的委派函式。</param>
    /// <param name="nowUtc">目前計算使用的標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步作業取消的語彙基元。</param>
    /// <returns>代表非同步評估作業的 <see cref="Task"/>。</returns>
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
                // 超標違規：檢查是否已達 DebounceSeconds 防抖動延遲
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
                // 恢復正常：移除超標違規時間，並解除已開立的告警
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

    /// <summary>
    /// 取得告警規則之識別代碼。
    /// </summary>
    /// <param name="rule">告警規則實體。</param>
    /// <returns>規則代碼字串。</returns>
    private static string RuleCode(AlarmRule rule) => rule.RuleCode;
}
