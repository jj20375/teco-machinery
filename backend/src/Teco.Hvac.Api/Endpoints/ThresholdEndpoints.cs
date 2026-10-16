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
    /// <summary>冰水出水溫度指標代碼。</summary>
    private const string SupplyTemp = nameof(ChillerSnapshot.ChilledWaterOutletTemperature);
    /// <summary>冰水入水溫度指標代碼。</summary>
    private const string ReturnTemp = nameof(ChillerSnapshot.ChilledWaterInletTemperature);
    /// <summary>冰水溫差 ΔT 指標代碼。</summary>
    private const string TempDiff = nameof(ChillerSnapshot.ChilledWaterTemperatureDifference);
    /// <summary>累計運轉時數指標代碼。</summary>
    private const string RunningHours = nameof(ChillerSnapshot.AccumulatedRunningHours);
    /// <summary>FCU 環境溫度指標代碼。</summary>
    private const string RoomTemp = nameof(FcuSnapshot.Temperature);

    /// <summary>
    /// 註冊告警門檻相關 HTTP 端點路由。
    /// </summary>
    /// <param name="app">WebApplication 實體。</param>
    public static void MapThresholdEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/thresholds").RequireAuthorization();

        // 取得指定冰水主機的各項門檻設定
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

        // 調整更新指定冰水主機的告警門檻設定
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

        // 取得全廠 FCU 室溫上下限設定
        group.MapGet("/fcus", async (ClaimsPrincipal principal, AlarmRepository alarms, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.thresholds", "read"))
                return Results.Forbid();
            var rules = await alarms.GetRulesAsync(AlarmDeviceType.Fcu, "*", ct);
            return Results.Ok(BuildFcuConfig(rules));
        });

        // 調整更新全廠 FCU 室溫上下限設定
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

    /// <summary>
    /// 套用單項指標邊界門檻值至 alarm_rule。
    /// value 為 null 代表畫面把這個欄位清空——直接刪掉對應規則，而不是存一個停用的。
    /// </summary>
    /// <param name="alarms">告警規則倉儲。</param>
    /// <param name="deviceType">設備類型。</param>
    /// <param name="scopeKey">規則適用範圍識別（例如 Modbus ID 或 "*"）。</param>
    /// <param name="deviceId">關聯設備識別碼。</param>
    /// <param name="metric">指標代碼。</param>
    /// <param name="op">比較運算子。</param>
    /// <param name="value">門檻數值；null 代表刪除規則。</param>
    /// <param name="ct">非同步作業取消語彙基元。</param>
    /// <param name="severity">告警嚴重程度，預設為 Warning。</param>
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

    /// <summary>
    /// 將冰水主機規則清單轉換組裝為前端畫面設定物件。
    /// </summary>
    /// <param name="code">主機代碼。</param>
    /// <param name="rules">生效中的規則列表。</param>
    /// <returns>前端格式的組態物件。</returns>
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

    /// <summary>
    /// 將 FCU 規則清單轉換組裝為前端畫面設定物件。
    /// </summary>
    /// <param name="rules">生效中的規則列表。</param>
    /// <returns>前端格式的組態物件。</returns>
    private static object BuildFcuConfig(IReadOnlyList<AlarmRule> rules)
    {
        double? Find(AlarmMetricOperator op) => rules.FirstOrDefault(r => r.Metric == RoomTemp && r.Operator == op)?.Threshold;
        return new { roomTempMin = Find(AlarmMetricOperator.LessThan), roomTempMax = Find(AlarmMetricOperator.GreaterThan) };
    }

    /// <summary>
    /// 冰水主機門檻更新請求模型。
    /// </summary>
    /// <param name="SupplyTempMin">冰水出水溫度下限 (°C)。</param>
    /// <param name="SupplyTempMax">冰水出水溫度上限 (°C)。</param>
    /// <param name="ReturnTempMin">冰水回水溫度下限 (°C)。</param>
    /// <param name="ReturnTempMax">冰水回水溫度上限 (°C)。</param>
    /// <param name="TempDiffMin">冰水溫差下限 (°C)。</param>
    /// <param name="TempDiffMax">冰水溫差上限 (°C)。</param>
    /// <param name="MaintenanceHoursLimit">保養週期時數間隔 (h)。</param>
    public sealed record ChillerThresholdRequest(
        double? SupplyTempMin, double? SupplyTempMax, double? ReturnTempMin, double? ReturnTempMax,
        double? TempDiffMin, double? TempDiffMax, double? MaintenanceHoursLimit);

    /// <summary>
    /// FCU 全廠門檻更新請求模型。
    /// </summary>
    /// <param name="RoomTempMin">室溫下限 (°C)。</param>
    /// <param name="RoomTempMax">室溫上限 (°C)。</param>
    public sealed record FcuThresholdRequest(double? RoomTempMin, double? RoomTempMax);
}
