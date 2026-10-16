using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Teco.Hvac.Contracts;

namespace Teco.Hvac.Collector;

/// <summary>
/// 時序資料落地節流策略 (ThrottlePolicy)。
/// 
/// 負責判定設備快照是否應當寫入 MariaDB 資料庫。
/// 
/// 【設計背景】
/// 即時推播（POST /internal/ingest）不受此處節流影響，每次硬體事件都會發送；
/// 但若每 5 秒將全廠所有設備（2 台主機 + 近百台 FCU）全數寫入資料庫，時序紀錄將高達 164 萬筆/天，會使資料庫磁碟與效能難以負荷。
/// 因此由本策略把關，僅在數值發生變化或達到時間上限時才進行落地儲存。
/// </summary>
public sealed class ThrottlePolicy(IOptions<CollectorOptions> options)
{
    /// <summary>採集器組態設定選項，包含各設備節流秒數與變化門檻值。</summary>
    private readonly CollectorOptions _options = options.Value;

    /// <summary>記錄各冰水主機（以 ModbusId 為鍵）最後一次成功寫入資料庫的標準 UTC 時間戳記快取。</summary>
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastChillerWrite = new();

    /// <summary>
    /// 記錄各 FCU（以 Channel, StationId, Position 複合鍵為鍵）最後一次成功寫入資料庫的時間與數值快照快取。
    /// </summary>
    private readonly ConcurrentDictionary<(Channel, byte, int), (DateTimeOffset LastWrite, FcuSnapshot LastValue)> _lastFcuWrite = new();

    /// <summary>
    /// 判定指定的冰水主機在當前時間點是否應寫入資料庫。
    /// 依據冰機節流週期（預設 10 秒）進行過濾。
    /// </summary>
    /// <param name="modbusId">冰水主機的 Modbus 站號 (1 或 2)。</param>
    /// <param name="now">目前判定使用的標準 UTC 時間戳記。</param>
    /// <returns>若距離上次寫入已達或超過節流秒數則更新快取並回傳 true；否則回傳 false。</returns>
    public bool ShouldWriteChiller(int modbusId, DateTimeOffset now)
    {
        if (!_lastChillerWrite.TryGetValue(modbusId, out var last) ||
            (now - last).TotalSeconds >= _options.ChillerThrottleSeconds)
        {
            _lastChillerWrite[modbusId] = now;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 判定指定的 FCU 在當前時間點是否應寫入資料庫。
    /// 
    /// 【觸發落地條件】
    /// 1. 週期已到：自上次寫入經過時間達到或超過節流秒數上限（預設 60 秒）。
    /// 2. 狀態變更：開關狀態 (SwitchStatus)、運轉模式 (Mode) 或風速 (FanSpeed) 任一發生變更。
    /// 3. 溫度突變：室內溫度變化絕對值達到或超過溫度變化門檻（預設 0.5°C）。
    /// </summary>
    /// <param name="snapshot">目前採集到的 FCU 設備快照。</param>
    /// <param name="now">目前判定使用的標準 UTC 時間戳記。</param>
    /// <returns>符合任一落地條件則更新快取並回傳 true；否則回傳 false。</returns>
    public bool ShouldWriteFcu(FcuSnapshot snapshot, DateTimeOffset now)
    {
        var key = (snapshot.Channel, snapshot.StationId, snapshot.Position);

        // 初次採集到此設備時直接落地
        if (!_lastFcuWrite.TryGetValue(key, out var last))
        {
            _lastFcuWrite[key] = (now, snapshot);
            return true;
        }

        // 1. 檢驗是否已達時間節流上限
        bool timeElapsed = (now - last.LastWrite).TotalSeconds >= _options.FcuThrottleSeconds;

        // 2. 檢驗運轉狀態（開關、模式、風速）是否異動
        bool stateChanged = last.LastValue.SwitchStatus != snapshot.SwitchStatus ||
                             last.LastValue.Mode != snapshot.Mode ||
                             last.LastValue.FanSpeed != snapshot.FanSpeed;

        // 3. 檢驗量測溫度是否達到變化門檻
        bool tempJumped = Math.Abs(last.LastValue.Temperature - snapshot.Temperature) >= _options.FcuTemperatureChangeThreshold;

        // 任一條件滿足即寫入資料庫
        if (timeElapsed || stateChanged || tempJumped)
        {
            _lastFcuWrite[key] = (now, snapshot);
            return true;
        }
        return false;
    }
}
