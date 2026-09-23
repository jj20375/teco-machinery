using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Contracts;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

public static class ChillerEndpoints
{
    public static void MapChillerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/chillers").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal principal, ChillerRepository repo, CurrentStateStore store) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.chillers", "read"))
                return Results.Forbid();
            return Results.Ok(await BuildListAsync(repo, store));
        });

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

        group.MapPatch("/{id:int}", UpdateDisplayName);
    }

    /// <summary>
    /// 比照 FcuEndpoints.UpdateDisplayName，讓場館自己填顯示名稱／自訂代碼；系統自己的
    /// code（CH-1）跟 modbus_id 不動，後者還要跟 Collector 的快照鍵值對得上，不能讓人改。
    /// 跟 FCU 的差別：device_chiller.display_name 是 NOT NULL（schema 既有設計），
    /// 所以這裡不接受留空，要清空的話請直接填回原本的名稱。
    /// </summary>
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

    public sealed record UpdateChillerDisplayNameRequest(string? DisplayName);

    /// <summary>
    /// 清單建置邏輯抽成共用方法——前台戰情室的公開唯讀端點（PublicEndpoints.cs）跟這裡要顯示
    /// 一模一樣的清單，不應該各寫一份、慢慢分歧。
    /// </summary>
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
