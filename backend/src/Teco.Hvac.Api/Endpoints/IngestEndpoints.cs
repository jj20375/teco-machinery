using Teco.Hvac.Api.Services;
using Teco.Hvac.Contracts;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// Collector → Api 的內部 ingest 端點。只信任 compose 內網 + 共享 token，
/// 不對外公開（compose 網路本身就不對外，token 是第二層防呆，防止設定錯誤時外流）。
/// </summary>
public static class IngestEndpoints
{
    public static void MapIngestEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/ingest").AddEndpointFilter(async (ctx, next) =>
        {
            var expectedToken = ctx.HttpContext.RequestServices
                .GetRequiredService<IConfiguration>()["Collector:InternalToken"];
            var actualToken = ctx.HttpContext.Request.Headers["X-Internal-Token"].ToString();

            if (string.IsNullOrEmpty(expectedToken) || actualToken != expectedToken)
            {
                return Results.Unauthorized();
            }
            return await next(ctx);
        });

        group.MapPost("/data", async (IngestPayload payload, CurrentStateStore store, TelemetryBroadcaster broadcaster, CancellationToken ct) =>
        {
            store.UpdateData(payload);
            await broadcaster.BroadcastLatestAsync(ct);
            return Results.Accepted();
        });

        group.MapPost("/connection", (ConnectionStatusPayload payload, CurrentStateStore store) =>
        {
            store.UpdateConnection(payload);
            return Results.Accepted();
        });
    }
}
