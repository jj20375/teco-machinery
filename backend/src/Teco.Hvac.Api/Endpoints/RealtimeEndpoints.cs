using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 即時遙測快照 API 端點群組。
/// 提供前端頁面初次載入或手動重新整理時的完整四部設備即時快照查詢。
/// </summary>
public static class RealtimeEndpoints
{
    /// <summary>
    /// 註冊即時快照相關 HTTP 端點路由。
    /// </summary>
    /// <param name="app">WebApplication 實體。</param>
    public static void MapRealtimeEndpoints(this WebApplication app)
    {
        // 取得四部設備之完整即時快照（含資料品質指標）
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
