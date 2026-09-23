using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;

namespace Teco.Hvac.Api.Endpoints;

public static class RealtimeEndpoints
{
    public static void MapRealtimeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/realtime/snapshot", (ClaimsPrincipal principal, CurrentStateStore store) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null) return Results.Forbid();
            if (!scope.Has("hvac.chillers", "read") && !scope.Has("hvac.fcus", "read")) return Results.Forbid();

            var snapshot = store.BuildRealtimeSnapshot();
            return snapshot is null
                ? Results.Ok(new { message = "尚未收到任何 Collector 資料" })
                : Results.Ok(snapshot);
        }).RequireAuthorization();
    }
}
