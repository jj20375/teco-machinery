using System.Text;
using Dapper;
using Teco.Hvac.Contracts;

namespace Teco.Hvac.Infrastructure.Repositories;

/// <summary>
/// 時序資料批次寫入。用單次 multi-row INSERT，不逐筆來回（計畫 P2 §寫入節流）。
/// 節流／變化偵測的決策（60 秒一筆或狀態變化即寫）由呼叫端（Collector 的
/// ThrottlePolicy）決定，這裡只負責把「已經決定要寫」的快照批次落地。
/// </summary>
public sealed class TimeSeriesWriter(TecoDbConnectionFactory factory)
{
    public async Task WriteChillerReadingsAsync(
        IReadOnlyList<(int DeviceId, DateTimeOffset ReceivedAtUtc, ChillerSnapshot Snapshot)> rows,
        CancellationToken ct = default)
    {
        if (rows.Count == 0) return;

        const string sqlTemplate = """
            INSERT INTO chiller_reading
                (device_id, ts, received_at, cooling_water_in, cooling_water_out,
                 chilled_water_in, chilled_water_out, chilled_water_delta,
                 input_current, input_voltage, high_pressure, low_pressure,
                 actual_rpm, running_hours, start_count, input_power_kw,
                 approach_temp, accumulated_kwh, load_percentage, water_control,
                 alarm_bits, read_status)
            VALUES {0}
            ON DUPLICATE KEY UPDATE ts = ts
            """;

        var sb = new StringBuilder();
        var parameters = new Dapper.DynamicParameters();
        for (int i = 0; i < rows.Count; i++)
        {
            var (deviceId, receivedAt, s) = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append($"(@deviceId{i}, @ts{i}, @receivedAt{i}, @cwIn{i}, @cwOut{i}, @chIn{i}, @chOut{i}, @chDelta{i}, " +
                      $"@current{i}, @voltage{i}, @hp{i}, @lp{i}, @rpm{i}, @hours{i}, @starts{i}, @kw{i}, " +
                      $"@approach{i}, @kwh{i}, @load{i}, @water{i}, @alarmBits{i}, @readStatus{i})");

            parameters.Add($"deviceId{i}", deviceId);
            parameters.Add($"ts{i}", s.UpdateTimeUtc.UtcDateTime);
            parameters.Add($"receivedAt{i}", receivedAt.UtcDateTime);
            parameters.Add($"cwIn{i}", s.CoolingWaterInletTemperature);
            parameters.Add($"cwOut{i}", s.CoolingWaterOutletTemperature);
            parameters.Add($"chIn{i}", s.ChilledWaterInletTemperature);
            parameters.Add($"chOut{i}", s.ChilledWaterOutletTemperature);
            parameters.Add($"chDelta{i}", s.ChilledWaterTemperatureDifference);
            parameters.Add($"current{i}", s.InputCurrent);
            parameters.Add($"voltage{i}", s.InputVoltage);
            parameters.Add($"hp{i}", s.HighPressure);
            parameters.Add($"lp{i}", s.LowPressure);
            parameters.Add($"rpm{i}", s.ActualRpm);
            parameters.Add($"hours{i}", s.AccumulatedRunningHours);
            parameters.Add($"starts{i}", s.AccumulatedStartCount);
            parameters.Add($"kw{i}", s.InputPowerKilowatt);
            parameters.Add($"approach{i}", s.ApproachTemperature);
            parameters.Add($"kwh{i}", s.AccumulatedEnergyKilowattHour);
            parameters.Add($"load{i}", s.LoadPercentage);
            parameters.Add($"water{i}", (int)s.WaterControl);
            parameters.Add($"alarmBits{i}", s.ToAlarmBits());
            parameters.Add($"readStatus{i}", (int)s.ReadStatus);
        }

        var sql = string.Format(sqlTemplate, sb.ToString());
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(sql, parameters);
    }

    public async Task WriteFcuReadingsAsync(
        IReadOnlyList<(int DeviceId, DateTimeOffset TsUtc, FcuSnapshot Snapshot, ReadStatus ReadStatus)> rows,
        CancellationToken ct = default)
    {
        if (rows.Count == 0) return;

        const string sqlTemplate = """
            INSERT INTO fcu_reading (device_id, ts, switch_status, mode, fan_speed, temperature, read_status)
            VALUES {0}
            ON DUPLICATE KEY UPDATE ts = ts
            """;

        var sb = new StringBuilder();
        var parameters = new Dapper.DynamicParameters();
        for (int i = 0; i < rows.Count; i++)
        {
            var (deviceId, ts, s, readStatus) = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append($"(@deviceId{i}, @ts{i}, @switch{i}, @mode{i}, @fan{i}, @temp{i}, @readStatus{i})");

            parameters.Add($"deviceId{i}", deviceId);
            parameters.Add($"ts{i}", ts.UtcDateTime);
            parameters.Add($"switch{i}", (int)s.SwitchStatus);
            parameters.Add($"mode{i}", (int)s.Mode);
            parameters.Add($"fan{i}", (int)s.FanSpeed);
            parameters.Add($"temp{i}", s.Temperature);
            parameters.Add($"readStatus{i}", (int)readStatus);
        }

        var sql = string.Format(sqlTemplate, sb.ToString());
        using var conn = await factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(sql, parameters);
    }
}
