using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 「告警門檻設定」畫面用的打包型端點。底層是 alarm_rule 這張通用表（AlarmEngine 拿同一張表
/// 去評估告警），但畫面是「一台冰水主機一組門檻」「FCU 全廠一組門檻」的固定形狀，這裡直接對應
/// 畫面形狀組裝，不做成通用規則 CRUD 列表——目前只有這兩個畫面在用，先不做用不到的彈性。
///
/// 權限走 hvac.thresholds，跟 hvac.chillers／hvac.fcus 分開——能看即時數據不代表能改告警門檻。
///
/// 冰水主機沒有水流量門檻：供應商 SDK 沒有水流量量測值。
/// FCU 端點叫 room-temp 不是 temp-diff：
/// 溫差＝室溫－設定溫度，但 Collector 沒有 FCU 設定溫度，這點在最早的規劃就決定用「絕對室溫
/// 上下限」（docs/BACKEND_INTEGRATION_PLAN.md §2.1），畫面文字要對應改成「室溫上下限」。
/// </summary>
public static class ThresholdEndpoints
{
    private const string SupplyTemp = nameof(ChillerSnapshot.ChilledWaterOutletTemperature);
    private const string ReturnTemp = nameof(ChillerSnapshot.ChilledWaterInletTemperature);
    private const string TempDiff = nameof(ChillerSnapshot.ChilledWaterTemperatureDifference);
    private const string RunningHours = nameof(ChillerSnapshot.AccumulatedRunningHours);
    private const string RoomTemp = nameof(FcuSnapshot.Temperature);

    public static void MapThresholdEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/thresholds").RequireAuthorization();

        group.MapGet("/chillers/{code}", async (
            ClaimsPrincipal principal, string code, ChillerRepository chillers, AlarmRepository alarms, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.thresholds", "read"))
                return Results.Forbid();

            var device = (await chillers.ListAsync(ct)).FirstOrDefault(d => d.Code == code);
            if (device is null) return Results.NotFound();

            var rules = await alarms.GetRulesAsync(AlarmDeviceType.Chiller, device.ModbusId.ToString(), ct);
            return Results.Ok(BuildChillerConfig(code, rules));
        });

        group.MapPut("/chillers/{code}", async (
            ClaimsPrincipal principal, string code, ChillerThresholdRequest request,
            ChillerRepository chillers, AlarmRepository alarms, ChillerMaintenanceRepository maintenance,
            CurrentStateStore store, OperationLogger opLog, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.thresholds", "update"))
                return Results.Forbid();

            var device = (await chillers.ListAsync(ct)).FirstOrDefault(d => d.Code == code);
            if (device is null) return Results.NotFound();

            if (request.MaintenanceHoursLimit is { } interval && (interval < 1 || interval != Math.Floor(interval)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["maintenanceHoursLimit"] = ["保養間隔要是大於 0 的整數小時。"],
                });
            }

            var scopeKey = device.ModbusId.ToString();
            var before = BuildChillerConfig(code, await alarms.GetRulesAsync(AlarmDeviceType.Chiller, scopeKey, ct));

            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, device.Id, SupplyTemp, AlarmMetricOperator.LessThan, request.SupplyTempMin, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, device.Id, SupplyTemp, AlarmMetricOperator.GreaterThan, request.SupplyTempMax, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, device.Id, ReturnTemp, AlarmMetricOperator.LessThan, request.ReturnTempMin, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, device.Id, ReturnTemp, AlarmMetricOperator.GreaterThan, request.ReturnTempMax, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, device.Id, TempDiff, AlarmMetricOperator.LessThan, request.TempDiffMin, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, device.Id, TempDiff, AlarmMetricOperator.GreaterThan, request.TempDiffMax, ct);
            // 保養間隔：門檻值是「距上次保養多少小時」，不是主機總時數（判定見 AlarmEngine.EvaluateMaintenanceAsync）。
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, device.Id, RunningHours, AlarmMetricOperator.GreaterThan,
                request.MaintenanceHoursLimit, ct, severity: AlarmSeverity.Info);
            if (request.MaintenanceHoursLimit is not null)
            {
                // 第一次設定就以當下時數起算；Collector 也會補做，但要等它約一分鐘後重新載入規則，
                // 這段期間保養狀態會顯示「尚未開始計算」，容易讓人以為沒存成功。
                if (ChillerMaintenanceEndpoints.GetLiveHours(store, device) is { } hours)
                    await maintenance.InitBaselineIfMissingAsync(device.Id, hours, DateTimeOffset.UtcNow, ct);
            }
            else
            {
                // 取消保養提醒時把亮著的燈一起熄掉，不然沒有間隔可比，燈會永遠亮著。
                await maintenance.CloseActiveAlarmAsync(device.Id, scope.UserId, DateTimeOffset.UtcNow, "已取消保養提醒設定", ct);
            }

            var after = BuildChillerConfig(code, await alarms.GetRulesAsync(AlarmDeviceType.Chiller, scopeKey, ct));
            await opLog.LogAsync(scope, "hvac.threshold.chiller.update", "device_chiller", code,
                $"調整了「{device.DisplayName}」的告警門檻設定", before: before, after: after, ct: ct);

            return Results.Ok(after);
        });

        group.MapGet("/fcus", async (ClaimsPrincipal principal, AlarmRepository alarms, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.thresholds", "read"))
                return Results.Forbid();
            var rules = await alarms.GetRulesAsync(AlarmDeviceType.Fcu, "*", ct);
            return Results.Ok(BuildFcuConfig(rules));
        });

        group.MapPut("/fcus", async (
            ClaimsPrincipal principal, FcuThresholdRequest request, AlarmRepository alarms, OperationLogger opLog, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.thresholds", "update"))
                return Results.Forbid();

            var before = BuildFcuConfig(await alarms.GetRulesAsync(AlarmDeviceType.Fcu, "*", ct));

            await ApplyBoundAsync(alarms, AlarmDeviceType.Fcu, "*", null, RoomTemp, AlarmMetricOperator.LessThan, request.RoomTempMin, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Fcu, "*", null, RoomTemp, AlarmMetricOperator.GreaterThan, request.RoomTempMax, ct);

            var after = BuildFcuConfig(await alarms.GetRulesAsync(AlarmDeviceType.Fcu, "*", ct));
            await opLog.LogAsync(scope, "hvac.threshold.fcu.update", "alarm_rule", "fcu-global",
                "調整了 FCU 全廠室溫上下限設定", before: before, after: after, ct: ct);

            return Results.Ok(after);
        });
    }

    /// <summary>value 為 null 代表畫面把這個欄位清空——直接刪掉對應規則，而不是存一個停用的。</summary>
    private static async Task ApplyBoundAsync(
        AlarmRepository alarms, AlarmDeviceType deviceType, string scopeKey, int? deviceId, string metric, AlarmMetricOperator op,
        double? value, CancellationToken ct, AlarmSeverity severity = AlarmSeverity.Warning)
    {
        if (value is null)
        {
            await alarms.DeleteRuleAsync(deviceType, scopeKey, metric, op, ct);
        }
        else
        {
            await alarms.UpsertRuleAsync(new AlarmRule
            {
                DeviceType = deviceType, Scope = scopeKey, Metric = metric, Operator = op,
                Threshold = value.Value, Severity = severity, DebounceSeconds = 60, IsEnabled = true,
            }, ct);
        }
        var keep = value is null ? null : AlarmRule.BuildRuleCode(metric, op, value.Value);
        await alarms.CloseStaleThresholdEventsAsync(deviceType, deviceId, metric, op, keep, DateTimeOffset.UtcNow, ct);
    }

    private static object BuildChillerConfig(string code, IReadOnlyList<AlarmRule> rules)
    {
        double? Find(string metric, AlarmMetricOperator op) =>
            rules.FirstOrDefault(r => r.Metric == metric && r.Operator == op)?.Threshold;

        return new
        {
            chillerCode = code,
            supplyTempMin = Find(SupplyTemp, AlarmMetricOperator.LessThan),
            supplyTempMax = Find(SupplyTemp, AlarmMetricOperator.GreaterThan),
            returnTempMin = Find(ReturnTemp, AlarmMetricOperator.LessThan),
            returnTempMax = Find(ReturnTemp, AlarmMetricOperator.GreaterThan),
            tempDiffMin = Find(TempDiff, AlarmMetricOperator.LessThan),
            tempDiffMax = Find(TempDiff, AlarmMetricOperator.GreaterThan),
            maintenanceHoursLimit = Find(RunningHours, AlarmMetricOperator.GreaterThan),
        };
    }

    private static object BuildFcuConfig(IReadOnlyList<AlarmRule> rules)
    {
        double? Find(AlarmMetricOperator op) => rules.FirstOrDefault(r => r.Metric == RoomTemp && r.Operator == op)?.Threshold;
        return new { roomTempMin = Find(AlarmMetricOperator.LessThan), roomTempMax = Find(AlarmMetricOperator.GreaterThan) };
    }

    public sealed record ChillerThresholdRequest(
        double? SupplyTempMin, double? SupplyTempMax, double? ReturnTempMin, double? ReturnTempMax,
        double? TempDiffMin, double? TempDiffMax, double? MaintenanceHoursLimit);

    public sealed record FcuThresholdRequest(double? RoomTempMin, double? RoomTempMax);
}
