using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Api.Services.Diagnostics;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 平台「系統診斷」：現場接通驗證用，只給 platform scope 且持有 platform.diagnostics:read 的帳號。
/// 內容包含通道 IP、原始讀值這類場館使用者不需要看到的維運資訊，所以刻意不放在場館後台，
/// 更不能放進 /api/v1/public/*。全部唯讀。
/// </summary>
public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/platform/diagnostics").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal principal, DiagnosticsService service, CancellationToken ct) =>
        {
            if (!CanRead(principal)) return Results.Forbid();
            return Results.Ok(await service.BuildAsync(ct));
        });

        group.MapGet("/raw", (ClaimsPrincipal principal, CurrentStateStore store) =>
        {
            if (!CanRead(principal)) return Results.Forbid();
            var snapshot = store.BuildRealtimeSnapshot();
            return snapshot is null
                ? Results.Ok(new { message = "尚未收到任何 Collector 資料" })
                : Results.Ok(snapshot);
        });
    }

    private static bool CanRead(ClaimsPrincipal principal) =>
        RequestScope.TryRead(principal, out var scope) && scope is not null &&
        scope.IsPlatform && scope.Has("platform.diagnostics", "read");
}
