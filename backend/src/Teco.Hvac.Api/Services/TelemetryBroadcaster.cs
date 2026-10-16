using Microsoft.AspNetCore.SignalR;
using Teco.Hvac.Api.Hubs;

namespace Teco.Hvac.Api.Services;

/// <summary>
/// 遙測資料廣播服務。
/// 把 CurrentStateStore 的最新快照廣播給所有已連線的 SignalR 客戶端。
/// </summary>
/// <param name="hubContext">SignalR TelemetryHub 上下文。</param>
/// <param name="store">目前即時遙測狀態儲存庫。</param>
public sealed class TelemetryBroadcaster(IHubContext<TelemetryHub> hubContext, CurrentStateStore store)
{
    /// <summary>
    /// 從 <see cref="CurrentStateStore"/> 組裝最新快照並透過 SignalR 推播 "telemetry" 事件。
    /// </summary>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    public async Task BroadcastLatestAsync(CancellationToken ct = default)
    {
        var snapshot = store.BuildRealtimeSnapshot();
        if (snapshot is null) return;
        await hubContext.Clients.All.SendAsync("telemetry", snapshot, ct);
    }
}
