using Microsoft.AspNetCore.SignalR;

namespace Teco.Hvac.Api.Hubs;

/// <summary>
/// 即時推播用的 SignalR Hub。前台一律先用 REST 的 /api/v1/realtime/snapshot 取初值，
/// 之後靠這裡的 "telemetry" 事件收到 RealtimeSnapshot 更新（由 IngestEndpoints 觸發廣播）。
/// 目前不需要 client → server 方法，保留這個類別作為未來擴充點（例如訂閱特定樓層）。
/// </summary>
public sealed class TelemetryHub : Hub;
