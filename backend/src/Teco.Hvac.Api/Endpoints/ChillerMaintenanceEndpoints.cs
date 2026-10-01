using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 冰水主機保養提醒（「機車換機油」模式），判定在 Collector 的 AlarmEngine.EvaluateMaintenanceAsync。
/// 這裡負責查保養狀態與「保養完成・重置」。保養間隔本身仍在「告警門檻設定」畫面設定（ThresholdEndpoints）。
/// 重置權限刻意用 hvac.thresholds:update，跟設定保養間隔的是同一批人（2026-10-01 確認）。
/// </summary>
public static class ChillerMaintenanceEndpoints
{
    public static void MapChillerMaintenanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/chillers/{id:int}/maintenance").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal principal, int id, ChillerRepository chillers, AlarmRepository alarms,
            ChillerMaintenanceRepository maintenance, CurrentStateStore store, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.chillers", "read"))
                return Results.Forbid();

            var device = (await chillers.ListAsync(ct)).FirstOrDefault(d => d.Id == id);
            if (device is null) return Results.NotFound();

            var intervalHours = await GetIntervalHoursAsync(alarms, device, ct);
            var state = await maintenance.GetStateAsync(id, ct);
            var currentHours = GetLiveHours(store, device) ?? await maintenance.GetLatestRecordedHoursAsync(id, ct);
            int? sinceService = currentHours is { } cur && state.BaselineHours is { } b ? Math.Max(0, cur - (int)b) : null;
            var isDue = await alarms.TryFindActiveAsync(AlarmDeviceType.Chiller, id, ChillerMaintenanceRepository.MaintenanceRuleCode, ct);
            var logs = await maintenance.ListLogsAsync(id, 10, ct);

            return Results.Ok(new
            {
                chillerId = id,
                intervalHours,
                baselineHours = state.BaselineHours,
                baselineAt = state.BaselineAt.AsUtcOffset(),
                currentHours,
                hoursSinceService = sinceService,
                remainingHours = intervalHours is { } iv && sinceService is { } s ? iv - s : (int?)null,
                isDue,
                history = logs.Select(l => new
                {
                    l.Id,
                    performedAt = l.PerformedAt.AsUtcOffset(),
                    l.PerformedByName,
                    l.HoursAtReset,
                    l.HoursSincePrevious,
                    l.Memo,
                }),
            });
        });

        group.MapPost("/reset", async (ClaimsPrincipal principal, int id, ResetMaintenanceRequest? request,
            ChillerRepository chillers, ChillerMaintenanceRepository maintenance, CurrentStateStore store,
            OperationLogger opLog, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.thresholds", "update"))
                return Results.Forbid();

            var device = (await chillers.ListAsync(ct)).FirstOrDefault(d => d.Id == id);
            if (device is null) return Results.NotFound();

            var memo = request?.Memo?.Trim();
            if (memo is { Length: > 255 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["memo"] = ["備註最多 255 個字。"] });
            if (string.IsNullOrEmpty(memo)) memo = null;

            // 主機離線時退回最後一筆讀取成功的時數；連這個都沒有，就沒有基準點可記，不能重置。
            var currentHours = GetLiveHours(store, device) ?? await maintenance.GetLatestRecordedHoursAsync(id, ct);
            if (currentHours is null)
                return Results.Problem("目前無法取得這台冰水主機的累積運轉時數，請等主機連線後再操作。", statusCode: 409);

            var result = await maintenance.ResetAsync(id, currentHours.Value, scope.UserId, DateTimeOffset.UtcNow, memo, ct);

            await opLog.LogAsync(scope, "hvac.chiller.maintenance.reset", "device_chiller", device.Code,
                $"將「{device.DisplayName}」標記為保養完成（累積 {currentHours}h" +
                (result.HoursSincePrevious is { } s ? $"，距上次保養 {s}h" : "") + "），保養時數重新計算",
                after: new { hoursAtReset = currentHours, result.HoursSincePrevious, memo }, ct: ct);

            return Results.Ok(new { hoursAtReset = currentHours, result.HoursSincePrevious, closedAlarmEventId = result.ClosedAlarmEventId });
        });
    }

    internal static int? GetLiveHours(CurrentStateStore store, DeviceChiller device)
    {
        var latest = store.GetLatest();
        var live = latest is null ? null : device.ModbusId switch { 1 => latest.Hanbell1, 2 => latest.Hanbell2, _ => null };
        return live is { ReadStatus: ReadStatus.Success } ? live.AccumulatedRunningHours : null;
    }

    private static async Task<int?> GetIntervalHoursAsync(AlarmRepository alarms, DeviceChiller device, CancellationToken ct)
    {
        var rules = await alarms.GetRulesAsync(AlarmDeviceType.Chiller, device.ModbusId.ToString(), ct);
        var rule = rules.FirstOrDefault(r =>
            r.Metric == ChillerMaintenanceRepository.MaintenanceRuleCode && r.Operator == AlarmMetricOperator.GreaterThan);
        return rule is null ? null : (int)rule.Threshold;
    }

    public sealed record ResetMaintenanceRequest(string? Memo);
}
