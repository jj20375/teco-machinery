using Microsoft.AspNetCore.SignalR;
using Teco.Hvac.Api.Hubs;

namespace Teco.Hvac.Api.Services;

/// <summary>把 CurrentStateStore 的最新快照廣播給所有已連線的 SignalR 客戶端。</summary>
public sealed class TelemetryBroadcaster(IHubContext<TelemetryHub> hubContext, CurrentStateStore store)
{
    public async Task BroadcastLatestAsync(CancellationToken ct = default)
    {
        var snapshot = store.BuildRealtimeSnapshot();
        if (snapshot is null) return;
        await hubContext.Clients.All.SendAsync("telemetry", snapshot, ct);
    }
}
