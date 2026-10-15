using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teco.Hvac.Contracts;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Collector;

/// <summary>
/// DLL 沒有提供「整體健康」API（無原始碼、無法修改），這裡自己補一層：
/// 追蹤每個通道最後一次成功讀取的時間、連線狀態進入新區段就寫 channel_health，
/// 並曝露判斷結果給 /healthz（Docker healthcheck 用）與「完全沒事件太久要不要自我重建」的判斷。
/// </summary>
public sealed class ChannelWatchdog(
    ChannelHealthRepository healthRepository,
    IOptions<CollectorOptions> options,
    ILogger<ChannelWatchdog> logger)
{
    private readonly CollectorOptions _options = options.Value;
    private readonly ConcurrentDictionary<Channel, DateTimeOffset> _lastSuccessUtc = new();
    private readonly ConcurrentDictionary<Channel, RecordedSegment?> _recorded = new();
    private readonly ConcurrentDictionary<Channel, ReadStatus> _lastReadStatus = new();
    private DateTimeOffset _lastDataEventUtc = DateTimeOffset.UtcNow;

    /// <summary>最後一次收到「資料事件」的時間（含讀取失敗的資料事件）。</summary>
    public DateTimeOffset LastDataEventUtc => _lastDataEventUtc;

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
    /// channel_health 只記「區段」：進入連線失敗（含從已連線斷線）、恢復已連線，各寫一列。
    /// 供應商 DLL 連不上時每約 5 秒會 Connecting → ConnectFailed → Connecting 來回切換，
    /// 以前每次切換都寫一列，設備沒接上的 13 天在正式機累積了 57 萬筆；現在「連線中」視為過程不記錄，
    /// 斷線／連線失敗／已停止都算同一段「沒連上」，直到恢復已連線才開新的一段。
    /// 連線事件本身不帶 ReadStatus（那是資料事件才有的概念），這裡用該通道最後一次資料事件回報的 ReadStatus 補上。
    /// </summary>
    public async Task OnConnectionStatusChangedAsync(
        Channel channel, ConnectionState state, DateTimeOffset changedAtUtc, CancellationToken ct)
    {
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
                // Collector 重啟後先接續資料庫裡最後一段，不然每次重啟都會多寫一列「連線失敗」。
                var latest = await healthRepository.GetLatestAsync(channel, ct);
                last = latest is null ? null : new RecordedSegment(latest.Id, PhaseOf((ConnectionState)latest.ConnectionState));
            }

            if (last?.Phase == phase)
            {
                _recorded[channel] = last;
                logger.LogDebug("通道 {Channel} 連線狀態：{State}（與目前區段相同，不記錄）", channel, state);
                return;
            }

            logger.LogInformation("通道 {Channel} 連線狀態變化：{State}", channel, state);
            var readStatus = _lastReadStatus.GetValueOrDefault(channel, ReadStatus.NotRead);
            var id = await healthRepository.StartSegmentAsync(channel, state, readStatus, changedAtUtc, last?.Id, ct);
            _recorded[channel] = new RecordedSegment(id, phase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 不更新記憶體狀態，下一個事件會再試；也不讓例外中斷連線事件的消費迴圈。
            logger.LogWarning(ex, "通道 {Channel} 連線狀態 {State} 寫入 channel_health 失敗", channel, state);
        }
    }

    private enum Phase { Connected, NotConnected }

    private sealed record RecordedSegment(long Id, Phase? Phase);

    private static Phase? PhaseOf(ConnectionState state) => state switch
    {
        ConnectionState.Connected => Phase.Connected,
        ConnectionState.Connecting => null,
        _ => Phase.NotConnected,
    };

    /// <summary>供 /healthz 使用：任一通道超過門檻沒有成功讀取，視為降級（但服務仍存活）。</summary>
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
    /// 重建 Collector 時呼叫：重新計時。沒有這一步的話，一旦曾經超過門檻沒有資料事件（例如主機睡眠），
    /// 重建後 ShouldRestart 仍然是 true，監督迴圈會每秒重建好幾十次、永遠收不到資料。
    /// </summary>
    public void ResetEventClock(DateTimeOffset nowUtc) => _lastDataEventUtc = nowUtc;

    /// <summary>
    /// 超過門檻沒有收到「資料事件」，代表 Collector 本身可能卡死，需要重建。
    /// 只看資料事件、不看連線狀態變化：DLL 正常時就算設備全部斷線，仍然每 5 秒送一次資料事件（讀取狀態為失敗），
    /// 所以設備斷電不會被誤判；反過來，DLL 卡住時連線狀態可能一直在抖動（斷線、重連），
    /// 那些事件不能算「還活著」，不然自我保護永遠不會觸發。
    /// </summary>
    public bool ShouldRestart(DateTimeOffset nowUtc) =>
        (nowUtc - _lastDataEventUtc).TotalSeconds > _options.WatchdogRestartAfterSeconds;
}
