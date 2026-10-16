using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Contracts;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 冰水主機 (Chiller) 相關 HTTP API 端點群組。
/// 提供即時設備狀態清單、歷史遙測時序檢索與自訂顯示名稱維護功能。
/// </summary>
public static class ChillerEndpoints
{
    /// <summary>
    /// 註冊冰水主機相關 HTTP 端點路由。
    /// </summary>
    /// <param name="app">WebApplication 實體。</param>
    public static void MapChillerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/chillers").RequireAuthorization();

        // 查詢所有冰水主機列表與最新即時讀值
        group.MapGet("/", async (ClaimsPrincipal principal, ChillerRepository repo, CurrentStateStore store) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.chillers", "read"))
                return Results.Forbid();
            return Results.Ok(await BuildListAsync(repo, store));
        });

        // 查詢特定代碼冰水主機之歷史讀值趨勢
        group.MapGet("/{code}/history", async (ClaimsPrincipal principal, string code, DateTime from, DateTime to, string? interval,
            ChillerRepository repo, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.chillers", "read"))
                return Results.Forbid();

            var devices = await repo.ListAsync(ct);
            var device = devices.FirstOrDefault(d => d.Code == code);
            if (device is null) return Results.NotFound();

            var rows = await repo.GetHistoryAsync(device.Id, from.ToUniversalTime(), to.ToUniversalTime(), interval ?? "raw", ct);
            return Results.Ok(rows);
        });

        // 修改冰水主機自訂顯示名稱
        group.MapPatch("/{id:int}", UpdateDisplayName);
    }

    /// <summary>
    /// 更新冰水主機顯示名稱之處理處理器。
    /// 比照 FcuEndpoints.UpdateDisplayName，讓場館自己填顯示名稱／自訂代碼；系統自己的
    /// code（CH-1）跟 modbus_id 不動，後者還要跟 Collector 的快照鍵值對得上，不能讓人改。
    /// 跟 FCU 的差別：device_chiller.display_name 是 NOT NULL（schema 既有設計），
    /// 所以這裡不接受留空，要清空的話請直接填回原本的名稱。
    /// </summary>
    /// <param name="principal">使用者身分主體。</param>
    /// <param name="id">主機識別碼。</param>
    /// <param name="request">修改顯示名稱請求載體。</param>
    /// <param name="repo">冰水主機倉儲。</param>
    /// <param name="opLog">操作日誌服務。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <returns>HTTP 操作結果。</returns>
    private static async Task<IResult> UpdateDisplayName(
        ClaimsPrincipal principal, int id, UpdateChillerDisplayNameRequest request,
        ChillerRepository repo, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.chillers", "update"))
            return Results.Forbid();

        var devices = await repo.ListAsync(ct);
        var device = devices.FirstOrDefault(d => d.Id == id);
        if (device is null) return Results.NotFound();

        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 64)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayName"] = ["冰水主機的顯示名稱不能留空，且最多 64 個字。"],
            });
        }

        await repo.UpdateDisplayNameAsync(id, displayName, ct);

        await opLog.LogAsync(scope, "hvac.chillers.update", "chiller", id.ToString(),
            $"更新了冰水主機 {device.Code} 的顯示名稱（{device.DisplayName} → {displayName}）",
            before: new { device.DisplayName }, after: new { DisplayName = displayName }, ct: ct);

        return Results.NoContent();
    }

    /// <summary>
    /// 更新冰水主機顯示名稱之請求模型。
    /// </summary>
    /// <param name="DisplayName">新的顯示名稱。</param>
    public sealed record UpdateChillerDisplayNameRequest(string? DisplayName);

    /// <summary>
    /// 清單建置邏輯抽成共用方法——前台戰情室的公開唯讀端點（PublicEndpoints.cs）跟這裡要顯示
    /// 一模一樣的清單，不應該各寫一份、慢慢分歧。
    /// </summary>
    /// <param name="repo">冰水主機倉儲。</param>
    /// <param name="store">目前狀態儲存庫。</param>
    /// <returns>冰水主機列表與最新讀值物件集合。</returns>
    internal static async Task<object> BuildListAsync(ChillerRepository repo, CurrentStateStore store)
    {
        var devices = await repo.ListAsync();
        var latest = store.GetLatest();

        return devices.Select(d =>
        {
            var live = latest is null ? null : (d.ModbusId == 1 ? latest.Hanbell1 : d.ModbusId == 2 ? latest.Hanbell2 : null);
            var quality = store.BuildDataQuality(Channel.HanbellModbusGateway, live?.ReadStatus ?? ReadStatus.NotRead);
            return new
            {
                d.Id,
                d.Code,
                d.ModbusId,
                d.DisplayName,
                d.RatedCapacityRt,
                dataQuality = quality,
                value = live,
            };
        });
    }
}
