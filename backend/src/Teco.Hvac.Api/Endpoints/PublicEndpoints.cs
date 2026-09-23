using Teco.Hvac.Api.Services;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 前台戰情室（`/`）用的公開唯讀端點——那個頁面本來就設計成不用登入的大廳螢幕
/// （DashboardLayout.astro 完全沒有登入檢查），但即時監控資料的其他端點都要求 JWT。
/// 這裡刻意跟 <c>/api/v1/{chillers,fcus,alarms,floor-plan}</c> 分開路由、完全不掛
/// <c>RequireAuthorization()</c>，只回傳跟後台一樣的即時監控資料——不含使用者、操作紀錄、
/// 密碼等任何機敏資訊，也不提供任何寫入能力。清單的組裝邏輯直接重用各自端點的
/// <c>BuildListAsync</c>/<c>BuildPlacementsResponseAsync</c>，避免兩邊分歧。
///
/// 這是內部場館的看板顯示用途，不是對外公開網路服務；沒有做 rate limiting，
/// 若未來真的要曝露在公開網路上，需要另外評估。
/// </summary>
public static class PublicEndpoints
{
    public static void MapPublicEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/public");

        group.MapGet("/chillers", async (ChillerRepository repo, CurrentStateStore store) =>
            Results.Ok(await ChillerEndpoints.BuildListAsync(repo, store)));

        group.MapGet("/fcus", async (string? floor, FcuRepository repo, CurrentStateStore store, CancellationToken ct) =>
            Results.Ok(await FcuEndpoints.BuildListAsync(floor, repo, store, ct)));

        group.MapGet("/fcus/hourly-stats", async (DateTime? date, FcuRepository repo, CancellationToken ct) =>
            Results.Ok(await FcuEndpoints.BuildHourlyStatsAsync(date, repo, ct)));

        group.MapGet("/alarms", async (string? status, AlarmRepository repo,
            ChillerRepository chillers, FcuRepository fcus, CancellationToken ct) =>
            Results.Ok(await AlarmEndpoints.BuildListAsync(status, repo, chillers, fcus, ct)));

        group.MapGet("/floor-plan/{floor}/placements", async (string floor, FloorPlanRepository repo, CancellationToken ct) =>
        {
            if (!FloorPlanEndpoints.KnownFloors.Contains(floor, StringComparer.OrdinalIgnoreCase)) return Results.NotFound();
            return Results.Ok(await FloorPlanEndpoints.BuildPlacementsResponseAsync(floor, repo, ct));
        });
    }
}
