using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teco.Hvac.Contracts;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Collector;

/// <summary>
/// 通道健康度看門狗 (ChannelWatchdog)。
/// 
/// 供應商 DLL 未提供「整體健康診斷」API（且因無原始碼無法直接擴充），本類別提供監控層：
/// 1. 追蹤每個通訊通道最後一次成功讀取資料的時間。
/// 2. 連線狀態進入新區段時寫入資料庫 channel_health 資料表。
/// 3. 提供 /healthz 容器健康度檢查依據（判斷服務是否處於降級狀態）。
/// 4. 偵測是否長時間未收到資料事件，以判定是否需要觸發 Collector 自我重建或重啟。
/// </summary>
public sealed class ChannelWatchdog(
    ChannelHealthRepository healthRepository,
    IOptions<CollectorOptions> options,
    ILogger<ChannelWatchdog> logger)
{
    /// <summary>採集器組態選項實例，包含看門狗各項逾時門檻值。</summary>
    private readonly CollectorOptions _options = options.Value;

    /// <summary>記錄各通訊通道最後一次成功讀取 (ReadStatus.Success) 的標準 UTC 時間戳記快取。</summary>
    private readonly ConcurrentDictionary<Channel, DateTimeOffset> _lastSuccessUtc = new();

    /// <summary>記錄各通訊通道目前在資料庫中登記的連線狀態歷史區段資訊（區段 ID 與連線相位）。</summary>
    private readonly ConcurrentDictionary<Channel, RecordedSegment?> _recorded = new();

    /// <summary>記錄各通訊通道最後一次資料事件回報的讀取狀態 (ReadStatus)。</summary>
    private readonly ConcurrentDictionary<Channel, ReadStatus> _lastReadStatus = new();

    /// <summary>最後一次收到「資料事件」的標準 UTC 時間（包含讀取成功的事件與回報失敗的事件）。</summary>
    private DateTimeOffset _lastDataEventUtc = DateTimeOffset.UtcNow;

    /// <summary>取得最後一次收到資料事件的標準 UTC 時間戳記。</summary>
    public DateTimeOffset LastDataEventUtc => _lastDataEventUtc;

    /// <summary>
    /// 當採集核心收到資料事件時由外部呼叫此方法進行記帳。
    /// 更新最後收到事件時間戳記、通道最新讀取狀態，若讀取成功則同步刷新該通道的最後成功時間。
    /// </summary>
    /// <param name="channel">觸發資料事件的通訊通道（漢鐘閘道、DDC1 或 DDC2）。</param>
    /// <param name="readStatus">本輪讀取狀態（成功、失敗或未讀）。</param>
    /// <param name="nowUtc">目前作業之標準 UTC 時間戳記。</param>
    public void OnDataReceived(Channel channel, ReadStatus readStatus, DateTimeOffset nowUtc)
    {
        _lastDataEventUtc = nowUtc;
        _lastReadStatus[channel] = readStatus;
        if (readStatus == ReadStatus.Success)
        {
            _lastSuccessUtc[channel] = nowUtc;
        }
    }

    /// <summary>
    /// 當通訊通道的連線狀態發生變更時進行評估並記錄歷史區段。
    /// 
    /// 【區段記錄原則】
    /// channel_health 資料表僅記錄「連線區段」的起始與結束：
    /// 1. 供應商 DLL 連不上設備時約每 5 秒在 Connecting → ConnectFailed → Connecting 來回跳動。
    ///    本邏輯將「Connecting（連線中）」視為過渡狀態不記錄；「斷線、失敗、停止」統一套用為 NotConnected 相位。
    /// 2. 只有在連線相位真正發生轉變（如從 Connected 進入 NotConnected，或 NotConnected 恢復為 Connected）時才寫入資料庫開立新區段。
    /// 3. 連線事件不帶 ReadStatus，寫入時使用該通道最近一次資料事件所記錄的 _lastReadStatus 補足。
    /// </summary>
    /// <param name="channel">發生狀態變更的通訊通道。</param>
    /// <param name="state">硬體回報之全新連線狀態。</param>
    /// <param name="changedAtUtc">狀態變更發生的標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步作業的取消語彙基元。</param>
    /// <returns>代表非同步處理作業的 <see cref="Task"/>。</returns>
    public async Task OnConnectionStatusChangedAsync(
        Channel channel, ConnectionState state, DateTimeOffset changedAtUtc, CancellationToken ct)
    {
        // 將底層多種狀態歸納為連線相位（Connected / NotConnected / null 忽略過渡期）
        var phase = PhaseOf(state);
        if (phase is null)
        {
            logger.LogDebug("通道 {Channel} 連線狀態：{State}（過程狀態，不記錄）", channel, state);
            return;
        }

        try
        {
            if (!_recorded.TryGetValue(channel, out var last))
            {
                // Collector 重啟後先自資料庫查詢該通道最後一筆未閉合或最新區段，避免每次程序重啟時重複寫入「連線失敗」
                var latest = await healthRepository.GetLatestAsync(channel, ct);
                last = latest is null ? null : new RecordedSegment(latest.Id, PhaseOf((ConnectionState)latest.ConnectionState));
            }

            // 若目前相位與前一區段相同，代表仍屬同一次斷線或同一次連線期間，直接忽略
            if (last?.Phase == phase)
            {
                _recorded[channel] = last;
                logger.LogDebug("通道 {Channel} 連線狀態：{State}（與目前區段相同，不記錄）", channel, state);
                return;
            }

            logger.LogInformation("通道 {Channel} 連線狀態變化：{State}", channel, state);
            var readStatus = _lastReadStatus.GetValueOrDefault(channel, ReadStatus.NotRead);
            // 寫入新區段並更新記憶體快取
            var id = await healthRepository.StartSegmentAsync(channel, state, readStatus, changedAtUtc, last?.Id, ct);
            _recorded[channel] = new RecordedSegment(id, phase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 寫入失敗不更新記憶體快取，留待下次事件重試，確保消費者迴圈不被中斷
            logger.LogWarning(ex, "通道 {Channel} 連線狀態 {State} 寫入 channel_health 失敗", channel, state);
        }
    }

    /// <summary>內部通道狀態相位分類列舉。</summary>
    private enum Phase
    {
        /// <summary>已成功建立連線相位。</summary>
        Connected,
        /// <summary>未連線相位（包含連線失敗、中斷或停止）。</summary>
        NotConnected
    }

    /// <summary>
    /// 記錄於記憶體中的已寫入區段記錄物件。
    /// </summary>
    /// <param name="Id">在資料庫 channel_health 資料表中的主鍵 ID。</param>
    /// <param name="Phase">該區段所代表的連線相位。</param>
    private sealed record RecordedSegment(long Id, Phase? Phase);

    /// <summary>
    /// 將原始連線狀態轉換為內部判斷用之相位。
    /// </summary>
    /// <param name="state">硬體回報之連線狀態。</param>
    /// <returns>若為 Connecting 則回傳 null（代表過渡中不予記錄）；否則回傳對應的 Connected 或 NotConnected。</returns>
    private static Phase? PhaseOf(ConnectionState state) => state switch
    {
        ConnectionState.Connected => Phase.Connected,
        ConnectionState.Connecting => null,
        _ => Phase.NotConnected,
    };

    /// <summary>
    /// 提供健康檢查 (/healthz) 判斷依據：
    /// 檢驗系統內所有通訊通道，若有任一通道超過門檻時間未曾成功讀取，判定為「降級 (Degraded)」狀態。
    /// </summary>
    /// <param name="nowUtc">目前檢查之標準 UTC 時間戳記。</param>
    /// <returns>若任一通道逾時未成功讀取則回傳 true；全部正常讀取則回傳 false。</returns>
    public bool IsDegraded(DateTimeOffset nowUtc)
    {
        foreach (Channel channel in Enum.GetValues<Channel>())
        {
            if (!_lastSuccessUtc.TryGetValue(channel, out var last) ||
                (nowUtc - last).TotalSeconds > _options.WatchdogStaleSeconds)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 重設最後收到事件的計時基準點。
    /// 當採集器執行自我重建時呼叫，防止因過往逾時殘留導致監督迴圈陷入每秒瘋狂重建的死迴圈。
    /// </summary>
    /// <param name="nowUtc">重設的標準 UTC 時間戳記。</param>
    public void ResetEventClock(DateTimeOffset nowUtc) => _lastDataEventUtc = nowUtc;

    /// <summary>
    /// 判定採集器是否超過門檻時間完全未收到任何資料事件而需要自我重建。
    /// 【注意】：此判定僅依賴資料事件，不看連線狀態事件；因為設備即便全數斷線，DLL 正常運作下仍會每 5 秒回報「讀取失敗」事件。
    /// 若完全收不到任何資料事件，即代表 DLL 內部輪詢線程已處於卡死狀態。
    /// </summary>
    /// <param name="nowUtc">當前計算之標準 UTC 時間戳記。</param>
    /// <returns>若超出設定之重啟門檻秒數則回傳 true，否則回傳 false。</returns>
    public bool ShouldRestart(DateTimeOffset nowUtc) =>
        (nowUtc - _lastDataEventUtc).TotalSeconds > _options.WatchdogRestartAfterSeconds;
}
