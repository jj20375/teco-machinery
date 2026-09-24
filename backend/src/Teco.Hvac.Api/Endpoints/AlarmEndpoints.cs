using System.Security.Claims;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Api.Endpoints;

public static class AlarmEndpoints
{
    /// <summary>漢鐘 14 個硬體旗標（RuleCode 就是旗標名稱本身）→ 人話標籤，供告警清單顯示。</summary>
    private static readonly Dictionary<string, string> ChillerAlarmLabels = new(StringComparer.Ordinal)
    {
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsChilledWaterFlowAbnormal)] = "冰水流量異常",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsCoolingWaterFlowAbnormal)] = "冷卻水流量異常",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsInverterAbnormal)] = "變頻器異常",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsCompressorOverload)] = "壓縮機過載",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsBearingTemperatureTooHigh)] = "軸承溫度過高",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsDischargeTemperatureTooHigh)] = "排氣溫度過高",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsMotorTemperatureTooHigh)] = "馬達溫度過高",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsPowerVoltageTooHigh)] = "電源電壓過高",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsPowerVoltageTooLow)] = "電源電壓過低",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsCurrentTooHigh)] = "電流過高",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsHighPressureTooHigh)] = "高壓過高",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsLowPressureTooLow)] = "低壓過低",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsOutletAntiFreezeAbnormal)] = "出水防凍異常",
        [nameof(Teco.Hvac.Contracts.ChillerSnapshot.IsInletAntiFreezeAbnormal)] = "入水防凍異常",
    };

    /// <summary>
    /// FCU 的 RuleCode 是 AlarmEngine 組出來的 "{Metric}.{Operator}.{Threshold}"（例："Temperature.0.28"），
    /// 目前只有室溫門檻（見 AlarmEngine.EvaluateFcuAsync 的決策註解），操作子 0=GreaterThan、1=LessThan。
    /// </summary>
    private static string DescribeFcuRule(string ruleCode)
    {
        var parts = ruleCode.Split('.', 3);
        if (parts.Length < 2 || parts[0] != nameof(Teco.Hvac.Contracts.FcuSnapshot.Temperature)) return ruleCode;
        return parts[1] == "0" ? "室內溫度過高" : parts[1] == "1" ? "室內溫度過低" : ruleCode;
    }

    /// <summary>
    /// 冰水主機除了 14 個硬體旗標，還有「告警門檻設定」畫面加的可設定門檻（供水/回水/溫差/
    /// 累積運轉時數），RuleCode 格式跟 FCU 一樣是 "{Metric}.{Operator}.{Threshold}"（見
    /// ThresholdEndpoints.ApplyBoundAsync／AlarmEngine.RuleCode）。這裡沒有對應到
    /// ChillerAlarmLabels 的 14 個硬體旗標鍵值，要另外解讀，不然告警清單只會顯示原始
    /// RuleCode 字串給使用者看。
    /// </summary>
    private static string DescribeChillerThresholdRule(string ruleCode)
    {
        var parts = ruleCode.Split('.', 3);
        if (parts.Length < 2) return ruleCode;
        var isMax = parts[1] == "0"; // 0=GreaterThan、1=LessThan，見 AlarmMetricOperator
        return parts[0] switch
        {
            nameof(Teco.Hvac.Contracts.ChillerSnapshot.ChilledWaterOutletTemperature) => isMax ? "出水溫度過高" : "出水溫度過低",
            nameof(Teco.Hvac.Contracts.ChillerSnapshot.ChilledWaterInletTemperature) => isMax ? "回水溫度過高" : "回水溫度過低",
            nameof(Teco.Hvac.Contracts.ChillerSnapshot.ChilledWaterTemperatureDifference) => isMax ? "溫度差過高" : "溫度差過低",
            nameof(Teco.Hvac.Contracts.ChillerSnapshot.AccumulatedRunningHours) => "累積運轉時數達保養門檻",
            _ => ruleCode,
        };
    }

    public static void MapAlarmEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/alarms").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal principal, string? status, DateTime? from, DateTime? to,
            AlarmRepository repo, ChillerRepository chillers, FcuRepository fcus, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null || !scope.Has("hvac.alarms", "read"))
                return Results.Forbid();
            return Results.Ok(await BuildListAsync(status, repo, chillers, fcus, ct,
                from?.ToUniversalTime(), to?.ToUniversalTime()));
        });

        group.MapPost("/{id:long}/ack", async (long id, AckRequest? body, ClaimsPrincipal principal,
            AlarmRepository repo, OperationLogger opLog, CancellationToken ct) =>
        {
            if (!RequestScope.TryRead(principal, out var scope) || scope is null) return Results.Forbid();
            // 確認告警是 hvac.alarms 的子功能，不是 CRUD 動作。
            if (!scope.Has("hvac.alarms", "update") || !scope.HasOption("hvac.alarms", "ack")) return Results.Forbid();

            await repo.AckAsync(id, scope.UserId, DateTimeOffset.UtcNow, body?.Memo, ct);
            await opLog.LogAsync(scope, "alarm.ack", "alarm_event", id.ToString(),
                string.IsNullOrWhiteSpace(body?.Memo) ? $"確認了告警 #{id}" : $"確認了告警 #{id}，備註：{body!.Memo}",
                after: new { Memo = body?.Memo }, ct: ct);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// 清單建置邏輯抽成共用方法，理由見 ChillerEndpoints.BuildListAsync 上的註解。
    /// `fromUtc`/`toUtc` 給告警歷史報表用；有給日期區間時放寬筆數上限（報表可能橫跨數天），
    /// 沒有給（即時告警清單／前台戰情室）維持原本 200 筆的上限。
    /// </summary>
    internal static async Task<object> BuildListAsync(
        string? status, AlarmRepository repo, ChillerRepository chillers, FcuRepository fcus, CancellationToken ct,
        DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        bool activeOnly = status is null or "active";
        var limit = fromUtc is not null || toUtc is not null ? 5000 : 200;
        var events = await repo.ListAsync(activeOnly, limit, fromUtc, toUtc, ct);

        // 前端(監控中心即時告警、告警報表、前台戰情室)要顯示設備名稱/位置/人話說明，不應該自己
        // 再去打 /chillers、/fcus 兜資料——比照 dataQuality 的設計原則，加值運算放後端算好。
        var chillerById = (await chillers.ListAsync(ct)).ToDictionary(d => d.Id);
        var fcuById = (await fcus.ListAsync(null, ct)).ToDictionary(d => d.Id);

        return events.Select(e =>
        {
            string deviceName, deviceCode, location, ruleLabel;
            if (e.DeviceType == AlarmDeviceType.Chiller && chillerById.TryGetValue(e.DeviceId, out var chiller))
            {
                deviceName = chiller.DisplayName;
                deviceCode = chiller.Code;
                location = "機房";
                ruleLabel = ChillerAlarmLabels.TryGetValue(e.RuleCode, out var label)
                    ? label
                    : DescribeChillerThresholdRule(e.RuleCode);
            }
            else if (e.DeviceType == AlarmDeviceType.Fcu && fcuById.TryGetValue(e.DeviceId, out var fcu))
            {
                deviceName = fcu.DisplayName ?? $"FCU#{fcu.Id}";
                deviceCode = fcu.ZoneCode ?? fcu.Floor;
                location = fcu.Floor;
                ruleLabel = DescribeFcuRule(e.RuleCode);
            }
            else
            {
                // 設備被停用(is_active=0)或刪除後，舊告警紀錄的關聯就查不到了；不要讓整支 API 500。
                deviceName = "(設備已移除)";
                deviceCode = "-";
                location = "-";
                ruleLabel = e.RuleCode;
            }

            return new
            {
                e.Id,
                e.DeviceType,
                e.DeviceId,
                e.RuleCode,
                e.Severity,
                startedAt = e.StartedAtUtc,
                endedAt = e.EndedAtUtc,
                e.PeakValue,
                e.AckByUserId,
                ackAt = e.AckAtUtc,
                e.Memo,
                deviceName,
                deviceCode,
                location,
                ruleLabel,
            };
        });
    }

    public sealed record AckRequest(string? Memo);
}
