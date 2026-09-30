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
/// 冰水主機的 flowMin/flowMax（水流量門檻）照設計稿可以設定、可以存，但目前不會觸發告警：供應商 SDK
/// 沒有水流量量測值，AlarmEngine 對「快照裡沒有對應欄位」的規則會直接跳過（見其 resolveMetricValue
/// 回傳 null 的處理）。存進 alarm_rule 不需要新欄位（metric 是字串），之後供應商提供數值時，只要
/// 在 ChillerSnapshot 加欄位並讓 AlarmEngine 的 resolver 回傳它，既有規則就自動生效。
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
    /// <summary>ChillerSnapshot 目前沒有這個屬性，所以不能用 nameof；名稱先訂好，等有資料來源時對應同名屬性。</summary>
    public const string ChilledWaterFlowRate = "ChilledWaterFlowRate";
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
            ChillerRepository chillers, AlarmRepository alarms, OperationLogger opLog, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.thresholds", "update"))
                return Results.Forbid();

            var device = (await chillers.ListAsync(ct)).FirstOrDefault(d => d.Code == code);
            if (device is null) return Results.NotFound();

            var scopeKey = device.ModbusId.ToString();
            var before = BuildChillerConfig(code, await alarms.GetRulesAsync(AlarmDeviceType.Chiller, scopeKey, ct));

            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, SupplyTemp, AlarmMetricOperator.LessThan, request.SupplyTempMin, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, SupplyTemp, AlarmMetricOperator.GreaterThan, request.SupplyTempMax, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, ReturnTemp, AlarmMetricOperator.LessThan, request.ReturnTempMin, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, ReturnTemp, AlarmMetricOperator.GreaterThan, request.ReturnTempMax, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, TempDiff, AlarmMetricOperator.LessThan, request.TempDiffMin, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, TempDiff, AlarmMetricOperator.GreaterThan, request.TempDiffMax, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, ChilledWaterFlowRate, AlarmMetricOperator.LessThan, request.FlowMin, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, ChilledWaterFlowRate, AlarmMetricOperator.GreaterThan, request.FlowMax, ct);
            // 保養時數只需要上限（累積運轉時數「超過」多少要保養），沒有下限的語意。
            await ApplyBoundAsync(alarms, AlarmDeviceType.Chiller, scopeKey, RunningHours, AlarmMetricOperator.GreaterThan,
                request.MaintenanceHoursLimit, ct, severity: AlarmSeverity.Info);

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

            await ApplyBoundAsync(alarms, AlarmDeviceType.Fcu, "*", RoomTemp, AlarmMetricOperator.LessThan, request.RoomTempMin, ct);
            await ApplyBoundAsync(alarms, AlarmDeviceType.Fcu, "*", RoomTemp, AlarmMetricOperator.GreaterThan, request.RoomTempMax, ct);

            var after = BuildFcuConfig(await alarms.GetRulesAsync(AlarmDeviceType.Fcu, "*", ct));
            await opLog.LogAsync(scope, "hvac.threshold.fcu.update", "alarm_rule", "fcu-global",
                "調整了 FCU 全廠室溫上下限設定", before: before, after: after, ct: ct);

            return Results.Ok(after);
        });
    }

    /// <summary>value 為 null 代表畫面把這個欄位清空——直接刪掉對應規則，而不是存一個停用的。</summary>
    private static async Task ApplyBoundAsync(
        AlarmRepository alarms, AlarmDeviceType deviceType, string scopeKey, string metric, AlarmMetricOperator op,
        double? value, CancellationToken ct, AlarmSeverity severity = AlarmSeverity.Warning)
    {
        if (value is null)
        {
            await alarms.DeleteRuleAsync(deviceType, scopeKey, metric, op, ct);
            return;
        }
        await alarms.UpsertRuleAsync(new AlarmRule
        {
            DeviceType = deviceType, Scope = scopeKey, Metric = metric, Operator = op,
            Threshold = value.Value, Severity = severity, DebounceSeconds = 60, IsEnabled = true,
        }, ct);
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
            flowMin = Find(ChilledWaterFlowRate, AlarmMetricOperator.LessThan),
            flowMax = Find(ChilledWaterFlowRate, AlarmMetricOperator.GreaterThan),
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
        double? TempDiffMin, double? TempDiffMax, double? FlowMin, double? FlowMax, double? MaintenanceHoursLimit);

    public sealed record FcuThresholdRequest(double? RoomTempMin, double? RoomTempMax);
}
