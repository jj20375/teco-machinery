using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 樓層平面圖的設備配置。這在先前只存在使用者瀏覽器的 localStorage，換裝置或清快取就消失；
/// 改由後端保存後才能跨裝置共用，前台的唯讀檢視也才看得到管理員配置的結果。
/// </summary>
public static class FloorPlanEndpoints
{
    internal static readonly string[] KnownFloors = ["B1", "B2"];

    public static void MapFloorPlanEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/floor-plan").RequireAuthorization();

        group.MapGet("/{floor}/placements", GetPlacements);
        group.MapPut("/{floor}/placements", ReplacePlacements);
    }

    private static async Task<IResult> GetPlacements(
        ClaimsPrincipal principal, string floor, FloorPlanRepository repo, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.floor_plan", "read"))
            return Results.Forbid();
        if (!KnownFloors.Contains(floor, StringComparer.OrdinalIgnoreCase)) return Results.NotFound();
        return Results.Ok(await BuildPlacementsResponseAsync(floor, repo, ct));
    }

    /// <summary>清單建置邏輯抽成共用方法，理由見 ChillerEndpoints.BuildListAsync 上的註解。</summary>
    internal static async Task<object> BuildPlacementsResponseAsync(string floor, FloorPlanRepository repo, CancellationToken ct)
    {
        var placements = await repo.ListByFloorAsync(floor, ct);
        return new
        {
            floor,
            // version 為 null 代表這層還沒被配置過；PUT 時要原樣帶回來做樂觀鎖比對。
            version = placements.Count == 0 ? (DateTimeOffset?)null : placements.Max(p => p.UpdatedAtUtc),
            placements = placements.Select(ToDto),
        };
    }

    /// <summary>
    /// 整層覆寫。前端本來就是「改一改、按一次儲存」，逐台 PATCH 沒有意義；
    /// expectedVersion 對應先前 localStorage 版本裡「另一個頁面已更新配置」那道保護，
    /// 沒有它的話兩個人同時編輯會互相把對方的配置蓋掉。
    /// </summary>
    private static async Task<IResult> ReplacePlacements(
        ClaimsPrincipal principal, string floor, ReplacePlacementsRequest request,
        FloorPlanRepository repo, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.floor_plan", "update"))
            return Results.Forbid();
        if (!KnownFloors.Contains(floor, StringComparer.OrdinalIgnoreCase)) return Results.NotFound();

        var items = request.Placements ?? [];
        if (items.Select(p => (p.DeviceType, p.DeviceId)).Distinct().Count() != items.Length)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["placements"] = ["同一台設備不能在圖面上出現兩次。"],
            });
        }
        foreach (var item in items)
        {
            if (item.DeviceType is not (0 or 1))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["deviceType"] = ["設備類型只能是 0（冰水主機）或 1（FCU）。"],
                });
            }
            if (item.Rotation is < 0 or > 359)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["rotation"] = ["朝向角度必須介於 0~359。"],
                });
            }
            if (string.IsNullOrWhiteSpace(item.AreaId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["areaId"] = ["每個配置都必須指定分區。"],
                });
            }
        }

        var currentVersion = await repo.GetFloorVersionAsync(floor, ct);
        if (!VersionMatches(currentVersion, request.ExpectedVersion))
        {
            return Results.Json(new
            {
                message = "這個樓層的配置已被其他人更新，請重新載入後再存檔，以免覆蓋對方的修改。",
                currentVersion,
            }, statusCode: StatusCodes.Status409Conflict);
        }

        var placements = items.Select(p => new DeviceFloorPlacement
        {
            DeviceType = (AlarmDeviceType)p.DeviceType,
            DeviceId = p.DeviceId,
            Floor = floor,
            AreaId = p.AreaId.Trim(),
            X = p.X,
            Y = p.Y,
            Rotation = p.Rotation,
        }).ToList();

        var newVersion = await repo.ReplaceFloorAsync(floor, placements, scope.UserId, ct);
        await repo.SyncFcuZoneCodesAsync(floor, placements, ct);

        await opLog.LogAsync(scope, "hvac.floor_plan.update", "floor_plan", floor,
            $"更新了 {floor} 樓層的設備配置（共 {placements.Count} 台）",
            after: new { Floor = floor, Count = placements.Count }, ct: ct);

        return Results.Ok(new { floor, version = newVersion, count = placements.Count });
    }

    /// <summary>兩邊都是 null（整層還沒配置過）也算相符；時間比對到毫秒即可，DB 欄位就是 DATETIME(3)。</summary>
    private static bool VersionMatches(DateTimeOffset? current, DateTimeOffset? expected)
    {
        if (current is null && expected is null) return true;
        if (current is null || expected is null) return false;
        return Math.Abs((current.Value - expected.Value).TotalMilliseconds) < 1;
    }

    private static object ToDto(DeviceFloorPlacement p) => new
    {
        deviceType = (int)p.DeviceType,
        deviceId = p.DeviceId,
        areaId = p.AreaId,
        x = p.X,
        y = p.Y,
        rotation = p.Rotation,
    };

    public sealed record PlacementItem(int DeviceType, int DeviceId, string AreaId, decimal X, decimal Y, int Rotation);
    public sealed record ReplacePlacementsRequest(PlacementItem[]? Placements, DateTimeOffset? ExpectedVersion);
}
