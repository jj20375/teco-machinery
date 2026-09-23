using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teco.Hvac.Contracts;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Collector;

/// <summary>
/// DLL 沒有提供「整體健康」API（無原始碼、無法修改），這裡自己補一層：
/// 追蹤每個通道最後一次成功讀取的時間、連線狀態變化就寫 channel_health，
/// 並曝露判斷結果給 /healthz（Docker healthcheck 用）與「完全沒事件太久要不要自我重建」的判斷。
/// </summary>
public sealed class ChannelWatchdog(
    ChannelHealthRepository healthRepository,
    IOptions<CollectorOptions> options,
    ILogger<ChannelWatchdog> logger)
{
    private readonly CollectorOptions _options = options.Value;
    private readonly ConcurrentDictionary<Channel, DateTimeOffset> _lastSuccessUtc = new();
    private readonly ConcurrentDictionary<Channel, ConnectionState> _lastConnectionState = new();
    private readonly ConcurrentDictionary<Channel, ReadStatus> _lastReadStatus = new();
    private DateTimeOffset _lastAnyEventUtc = DateTimeOffset.UtcNow;

    public void OnDataReceived(Channel channel, ReadStatus readStatus, DateTimeOffset nowUtc)
    {
        _lastAnyEventUtc = nowUtc;
        _lastReadStatus[channel] = readStatus;
        if (readStatus == ReadStatus.Success)
        {
            _lastSuccessUtc[channel] = nowUtc;
        }
    }

    /// <summary>
    /// 連線事件本身不帶 ReadStatus（那是資料事件才有的概念），這裡用該通道
    /// 最後一次資料事件回報的 ReadStatus 補上，讓 channel_health 兩欄都有意義的值。
    /// </summary>
    public async Task OnConnectionStatusChangedAsync(
        Channel channel, ConnectionState state, DateTimeOffset changedAtUtc, CancellationToken ct)
    {
        _lastAnyEventUtc = changedAtUtc;
        bool isNewState = !_lastConnectionState.TryGetValue(channel, out var prev) || prev != state;
        _lastConnectionState[channel] = state;

        if (isNewState)
        {
            logger.LogInformation("通道 {Channel} 連線狀態變化：{State}", channel, state);
            var readStatus = _lastReadStatus.GetValueOrDefault(channel, ReadStatus.NotRead);
            await healthRepository.RecordAsync(channel, state, readStatus, changedAtUtc, ct);
        }
    }

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

    /// <summary>完全沒有收到任何事件（含失敗）超過門檻，代表 Collector 本身可能卡死，需要重建。</summary>
    public bool ShouldRestart(DateTimeOffset nowUtc) =>
        (nowUtc - _lastAnyEventUtc).TotalSeconds > _options.WatchdogRestartAfterSeconds;
}
