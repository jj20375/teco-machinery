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
    /// <summary>
    /// 註冊平台系統診斷相關端點。
    /// </summary>
    /// <param name="app">Web 應用程式實例，用於掛載診斷路由群組</param>
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        // 診斷端點群組 /api/v1/platform/diagnostics，強制要求 JWT 驗證
        var group = app.MapGroup("/api/v1/platform/diagnostics").RequireAuthorization();

        // 產出完整系統診斷報告（包含 Modbus 通道健康、時序寫入進度、快照時效與資料驗證結果）
        group.MapGet("/", async (ClaimsPrincipal principal, DiagnosticsService service, CancellationToken ct) =>
        {
            // 驗證是否具備平台診斷讀取權限
            if (!CanRead(principal)) return Results.Forbid();
            return Results.Ok(await service.BuildAsync(ct));
        });

        // 取得 Collector 最新原始快照（用於檢視底層設備原始工程單位數值）
        group.MapGet("/raw", (ClaimsPrincipal principal, CurrentStateStore store) =>
        {
            // 驗證是否具備平台診斷讀取權限
            if (!CanRead(principal)) return Results.Forbid();
            var snapshot = store.BuildRealtimeSnapshot();
            return snapshot is null
                ? Results.Ok(new { message = "尚未收到任何 Collector 資料" })
                : Results.Ok(snapshot);
        });
    }

    /// <summary>
    /// 檢查使用者身分識別是否具備平台診斷讀取權限 (platform.diagnostics:read)。
    /// </summary>
    /// <param name="principal">使用者身分主體</param>
    /// <returns>若為平台管理者且擁有診斷讀取權限則回傳 true；否則回傳 false</returns>
    private static bool CanRead(ClaimsPrincipal principal) =>
        RequestScope.TryRead(principal, out var scope) && scope is not null &&
        scope.IsPlatform && scope.Has("platform.diagnostics", "read");
}
