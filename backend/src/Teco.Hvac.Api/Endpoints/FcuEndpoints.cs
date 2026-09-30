using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Contracts;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

public static class FcuEndpoints
{
    public static void MapFcuEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/fcus").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal principal, string? floor, FcuRepository repo, CurrentStateStore store, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.fcus", "read"))
                return Results.Forbid();
            return Results.Ok(await BuildListAsync(floor, repo, store, ct));
        });

        group.MapGet("/{id:int}/history", async (ClaimsPrincipal principal, int id, DateTime from, DateTime to, string? interval, FcuRepository repo, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.fcus", "read"))
                return Results.Forbid();

            var device = await repo.FindAsync(id, ct);
            if (device is null) return Results.NotFound();

            var rows = await repo.GetHistoryAsync(id, from.ToUniversalTime(), to.ToUniversalTime(), interval ?? "raw", ct);
            return Results.Ok(rows);
        });

        group.MapGet("/hourly-stats", async (ClaimsPrincipal principal, DateTime? date, FcuRepository repo, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.fcus", "read"))
                return Results.Forbid();
            return Results.Ok(await BuildHourlyStatsAsync(date, repo, ct));
        });

        group.MapPatch("/{id:int}", UpdateDisplayName);
    }

    /// <summary>
    /// 讓場館自己填一組看得懂的代碼／名稱（例如現場設備標籤上的編號），跟系統自己的
    /// zone_code、(channel, station_id, position) 並存——系統這組是實體接線決定的、有意義，
    /// 不能被覆蓋掉，所以另外用 display_name 這個本來就存在但一直沒被使用的欄位放客戶的版本。
    /// 留空代表清回「未設定」，畫面會退回顯示系統編號。
    /// </summary>
    private static async Task<IResult> UpdateDisplayName(
        ClaimsPrincipal principal, int id, UpdateFcuDisplayNameRequest request,
        FcuRepository repo, OperationLogger opLog, CancellationToken ct)
    {
        if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.fcus", "update"))
            return Results.Forbid();

        var device = await repo.FindAsync(id, ct);
        if (device is null) return Results.NotFound();

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        if (displayName is { Length: > 64 })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayName"] = ["自訂代碼最多 64 個字。"],
            });
        }

        await repo.UpdateDisplayNameAsync(id, displayName, ct);

        var systemCode = device.ZoneCode ?? $"{device.Floor}-{device.Id}";
        await opLog.LogAsync(scope, "hvac.fcus.update", "fcu", id.ToString(),
            $"更新了 FCU {systemCode} 的自訂代碼（{device.DisplayName ?? "未設定"} → {displayName ?? "未設定"}）",
            before: new { DisplayName = device.DisplayName }, after: new { DisplayName = displayName }, ct: ct);

        return Results.NoContent();
    }

    public sealed record UpdateFcuDisplayNameRequest(string? DisplayName);

    /// <summary>
    /// 供應商程式裡的 FCU 編號（例如 FC_MC1_01），現場技術人員與供應商溝通時用的是這組名稱。
    /// 有連線時直接用供應商回報的 ID；沒連線時依說明書表 19 的命名規則「FC_MC{站號}_{兩位位置}」推算。
    /// 注意：這個編號在 DDC1、DDC2 之間會重複（說明書 6.1），畫面上一定要搭配 DDC 一起顯示。
    /// </summary>
    internal static string VendorCodeOf(byte stationId, int position) => $"FC_MC{stationId}_{position:00}";

    /// <summary>清單建置邏輯抽成共用方法，理由見 ChillerEndpoints.BuildListAsync 上的註解。</summary>
    internal static async Task<object> BuildListAsync(string? floor, FcuRepository repo, CurrentStateStore store, CancellationToken ct)
    {
        var devices = await repo.ListAsync(floor, ct);
        var latest = store.GetLatest();

        var liveByKey = latest is null
            ? new Dictionary<(Channel, byte, int), FcuSnapshot>()
            : latest.Ddc1.FcuList.Concat(latest.Ddc2.FcuList)
                .ToDictionary(f => (f.Channel, f.StationId, f.Position));

        var ddcReadStatus = new Dictionary<Channel, ReadStatus>
        {
            [Channel.Ddc1] = latest?.Ddc1.ReadStatus ?? ReadStatus.NotRead,
            [Channel.Ddc2] = latest?.Ddc2.ReadStatus ?? ReadStatus.NotRead,
        };

        return devices.Select(d =>
        {
            liveByKey.TryGetValue((d.Channel, d.StationId, d.Position), out var live);
            var quality = store.BuildDataQuality(d.Channel, ddcReadStatus[d.Channel]);
            return new
            {
                d.Id,
                d.Channel,
                d.StationId,
                d.Position,
                d.Address,
                vendorCode = live?.Id ?? VendorCodeOf(d.StationId, d.Position),
                d.Floor,
                d.ZoneCode,
                d.DisplayName,
                dataQuality = quality,
                value = live,
            };
        });
    }

    /// <summary>
    /// 每小時趨勢圖（前台戰情室的長條圖／折線圖）：給定一個本地日期（預設今天，容器 TZ 是
    /// Asia/Taipei），回傳 24 小時 × B1/B2 的「啟用中設備數」與「平均室溫」。
    /// 小時分桶跟時區換算都在這裡用 TimeZoneInfo.Local 做，理由見
    /// FcuRepository.GetReadingsForHourlyStatsAsync 上的註解。
    /// </summary>
    internal static async Task<object> BuildHourlyStatsAsync(DateTime? date, FcuRepository repo, CancellationToken ct)
    {
        var localTz = TimeZoneInfo.Local;
        var localDate = (date ?? DateTime.Now).Date;
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), localTz);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified), localTz);

        var rows = await repo.GetReadingsForHourlyStatsAsync(fromUtc, toUtc, ct);

        var runningByHourFloor = new Dictionary<(int Hour, string Floor), HashSet<int>>();
        var tempAggByHourFloor = new Dictionary<(int Hour, string Floor), (double Sum, int Count)>();

        foreach (var row in rows)
        {
            var localTs = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.Ts, DateTimeKind.Utc), localTz);
            var key = (localTs.Hour, row.Floor);

            if (row.SwitchStatus == (int)FcuSwitchStatus.On)
            {
                if (!runningByHourFloor.TryGetValue(key, out var devices))
                {
                    devices = [];
                    runningByHourFloor[key] = devices;
                }
                devices.Add(row.DeviceId);
            }

            if (row.ReadStatus == (int)ReadStatus.Success && row.Temperature is decimal temp)
            {
                var (sum, count) = tempAggByHourFloor.GetValueOrDefault(key, (0, 0));
                tempAggByHourFloor[key] = (sum + (double)temp, count + 1);
            }
        }

        int[] CountSeries(string floor) => Enumerable.Range(0, 24)
            .Select(h => runningByHourFloor.TryGetValue((h, floor), out var devices) ? devices.Count : 0)
            .ToArray();

        // 沒有任何成功讀值的小時回 null（前端折線圖畫成斷點），不要用 0°C 冒充「量到 0 度」。
        double?[] TempSeries(string floor) => Enumerable.Range(0, 24)
            .Select(h => tempAggByHourFloor.TryGetValue((h, floor), out var agg) && agg.Count > 0
                ? Math.Round(agg.Sum / agg.Count, 1)
                : (double?)null)
            .ToArray();

        return new
        {
            date = localDate.ToString("yyyy-MM-dd"),
            hours = Enumerable.Range(0, 24).Select(h => $"{h:D2}:00").ToArray(),
            b1Count = CountSeries("B1"),
            b2Count = CountSeries("B2"),
            b1Temp = TempSeries("B1"),
            b2Temp = TempSeries("B2"),
        };
    }
}
