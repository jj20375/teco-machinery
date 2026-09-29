using System.Net.Http.Json;
using Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure;
using Teco.Hvac.Infrastructure.Repositories;
using static Teco.Hvac.Api.Services.Diagnostics.DataValidationRules;

namespace Teco.Hvac.Api.Services.Diagnostics;

/// <summary>
/// 組出平台「系統診斷」頁的完整報告：現場接通時用來一眼確認「連上了沒、資料對不對、
/// 有沒有寫進資料庫、排程有沒有在跑」。Collector 程式本身不需要配合修改——資料全部來自
/// API 已經有的 CurrentStateStore、資料庫，以及 compose 內網直接打 Collector 的 /healthz。
///
/// 各段資料來源彼此獨立，任一段失敗（例如資料庫暫時連不上）只讓該段顯示錯誤，
/// 不讓整頁 500——診斷頁本身在系統半壞的時候最需要還能打開。
/// </summary>
public sealed class DiagnosticsService(
    CurrentStateStore store,
    ScheduledJobStatusStore jobs,
    DeviceRepository devices,
    FcuRepository fcus,
    ChannelHealthRepository channelHealth,
    DiagnosticsRepository diagnostics,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<DiagnosticsService> logger)
{
    public const string CollectorHttpClientName = "collector-health";
    private const int ListLimit = 50;

    // 模擬腳本只打 /internal/ingest，不會寫資料庫（正式環境是 Collector 自己寫），跑模擬時這項必定紅燈。
    private const string SimulationNote = "（若正在跑 simulate-live-data.sh，模擬資料不會寫進資料庫，這項紅燈屬預期。）";

    private static readonly Channel[] AllChannels = [Channel.HanbellModbusGateway, Channel.Ddc1, Channel.Ddc2];

    public static string ChannelName(Channel channel) => channel switch
    {
        Channel.HanbellModbusGateway => "漢鐘 Gateway（冰水主機 ×2）",
        Channel.Ddc1 => "DDC1（B1F FCU）",
        Channel.Ddc2 => "DDC2（B2F FCU）",
        _ => channel.ToString(),
    };

    public async Task<DiagnosticsReport> BuildAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (latest, previous, lastIngestAt, ingestCount) = store.GetIngestState();
        var checks = new List<DiagnosticCheck>();

        var collectorTask = GetCollectorStatusAsync(ct);
        var ingest = BuildIngest(latest, lastIngestAt, ingestCount, now, checks);
        BuildTimeChecks(latest, previous, checks);

        var channels = AllChannels.Select(c => BuildChannel(c, latest)).ToList();

        var chillerDevices = await TryAsync(() => devices.GetChillersByModbusIdAsync(ct), "讀取冰水主機主檔");
        var chillers = latest is null
            ? []
            : new[] { latest.Hanbell1, latest.Hanbell2 }
                .Select(c => BuildChiller(c, FindPrevious(previous, c.ModbusId), chillerDevices, checks)).ToList();

        var fcuDevices = await TryAsync(() => fcus.ListAsync(null, ct), "讀取 FCU 主檔");
        var coverage = latest is null || fcuDevices is null
            ? []
            : new[] { latest.Ddc1, latest.Ddc2 }.Select(d => BuildCoverage(d, fcuDevices, checks)).ToList();

        var persistence = await BuildPersistenceAsync(now, latest, checks, ct);
        var jobReports = BuildJobs(now, persistence);
        var history = await BuildHistoryAsync(ct);
        var collector = await collectorTask;

        var overall = Worst(
            new[] { collector.Level, ingest.Level }
                .Concat(channels.Select(c => c.Level))
                .Concat(checks.Select(c => c.Level))
                .Concat(jobReports.Select(j => j.Level)));

        return new DiagnosticsReport(
            now, overall, collector, ingest, channels, checks, coverage, chillers, persistence, jobReports, history);
    }

    // ------------------------------------------------------------------
    // Collector 程序本身
    // ------------------------------------------------------------------

    private sealed record CollectorHealthBody(string? Status);

    private async Task<CollectorStatus> GetCollectorStatusAsync(CancellationToken ct)
    {
        var url = configuration["Diagnostics:CollectorHealthUrl"] ?? "http://collector:8080/healthz";
        try
        {
            var client = httpClientFactory.CreateClient(CollectorHttpClientName);
            var body = await client.GetFromJsonAsync<CollectorHealthBody>(url, ct);
            return body?.Status switch
            {
                "healthy" => new CollectorStatus(LevelOk, true, body.Status, "Collector 運作中，三條通道最近都有成功讀取。"),
                "degraded" => new CollectorStatus(LevelWarn, true, body.Status,
                    $"Collector 活著，但至少一條通道超過 {ChannelStaleSeconds:0} 秒沒有成功讀取（看下方通道卡片是哪一條）。"),
                _ => new CollectorStatus(LevelWarn, true, body?.Status, "Collector 有回應，但狀態無法辨識。"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new CollectorStatus(LevelError, false, null,
                $"連不到 Collector（{url}）：容器可能停止或卡住。若正在跑 simulate-live-data.sh，腳本會刻意暫停 collector，這一項紅燈屬預期。");
        }
    }

    // ------------------------------------------------------------------
    // 推送與時間
    // ------------------------------------------------------------------

    private static IngestStatus BuildIngest(
        IngestPayload? latest, DateTimeOffset? lastIngestAt, long ingestCount, DateTimeOffset now, List<DiagnosticCheck> checks)
    {
        if (latest is null || lastIngestAt is null)
        {
            checks.Add(new("ingest", "推送", "Collector → API 推送", LevelError,
                "API 啟動後還沒收到任何一筆 Collector 資料。先確認 collector 容器在跑、log 有沒有連線錯誤。"));
            return new IngestStatus(LevelError, null, null, ingestCount, null, null);
        }

        var since = (now - lastIngestAt.Value).TotalSeconds;
        var level = since > IngestStaleSeconds ? LevelError : LevelOk;
        checks.Add(new("ingest", "推送", "Collector → API 推送", level,
            level == LevelOk
                ? $"最後一筆在 {since:0} 秒前收到，API 啟動後累計 {ingestCount} 筆。"
                : $"已經 {since:0} 秒沒收到新資料（正常約 5 秒一筆），Collector 可能停止或卡住。"));
        return new IngestStatus(level, lastIngestAt, since, ingestCount, latest.UpdateTimeUtc, latest.ReceivedAtUtc);
    }

    private static void BuildTimeChecks(IngestPayload? latest, IngestPayload? previous, List<DiagnosticCheck> checks)
    {
        if (latest is null) return;

        var skew = Math.Abs((latest.ReceivedAtUtc - latest.UpdateTimeUtc).TotalSeconds);
        var skewLevel = skew > ClockSkewErrorSeconds ? LevelError : skew > ClockSkewWarnSeconds ? LevelWarn : LevelOk;
        checks.Add(new("clock-skew", "時間", "設備時間與收到時間差", skewLevel,
            skewLevel == LevelOk
                ? $"相差 {skew:0.#} 秒，正常。"
                : $"相差 {skew / 3600:0.##} 小時（{skew:0} 秒）。整數小時的差距幾乎都是時區換算錯誤（例如 8 小時＝UTC/台北混用），要檢查 Collector 主機時區。"));

        if (previous is not null)
        {
            var frozen = previous.UpdateTimeUtc == latest.UpdateTimeUtc;
            checks.Add(new("update-time-advancing", "時間", "設備時間持續前進", frozen ? LevelWarn : LevelOk,
                frozen
                    ? "連續兩筆資料的 UpdateTime 完全相同，Collector 可能在重送舊資料。"
                    : $"上一筆 {previous.UpdateTimeUtc:HH:mm:ss} → 這一筆 {latest.UpdateTimeUtc:HH:mm:ss}（UTC）。"));
        }
    }

    // ------------------------------------------------------------------
    // 通道
    // ------------------------------------------------------------------

    private ChannelStatus BuildChannel(Channel channel, IngestPayload? latest)
    {
        store.TryGetConnectionStatus(channel, out var conn);
        ReadStatus? readStatus = latest is null
            ? null
            : channel switch
            {
                Channel.HanbellModbusGateway =>
                    latest.Hanbell1.ReadStatus == ReadStatus.Success || latest.Hanbell2.ReadStatus == ReadStatus.Success
                        ? ReadStatus.Success
                        : latest.Hanbell1.ReadStatus,
                Channel.Ddc1 => latest.Ddc1.ReadStatus,
                _ => latest.Ddc2.ReadStatus,
            };

        var quality = store.BuildDataQuality(channel, readStatus ?? ReadStatus.NotRead);
        string level;
        if (conn is null && latest is null) level = LevelUnknown;
        else if (!quality.IsConnected || readStatus != ReadStatus.Success) level = LevelError;
        else if (quality.StaleSeconds is null || quality.StaleSeconds > ChannelStaleSeconds) level = LevelWarn;
        else level = LevelOk;

        return new ChannelStatus(
            (int)channel, ChannelName(channel), level, conn?.Ip, conn?.Port,
            conn is null ? null : (int)conn.ConnectionState, quality.IsConnected,
            readStatus is null ? null : (int)readStatus.Value,
            quality.LastSuccessAtUtc, quality.StaleSeconds, conn?.TriggerTimeUtc);
    }

    // ------------------------------------------------------------------
    // 冰水主機逐欄
    // ------------------------------------------------------------------

    private static ChillerSnapshot? FindPrevious(IngestPayload? previous, int modbusId) =>
        previous is null ? null
            : previous.Hanbell1.ModbusId == modbusId ? previous.Hanbell1
            : previous.Hanbell2.ModbusId == modbusId ? previous.Hanbell2
            : null;

    private static ChillerFieldReport BuildChiller(
        ChillerSnapshot c, ChillerSnapshot? prev, IReadOnlyDictionary<int, DeviceChiller>? deviceMap, List<DiagnosticCheck> checks)
    {
        DeviceChiller? device = null;
        deviceMap?.TryGetValue(c.ModbusId, out device);
        var label = device is null ? $"冰水主機 ModbusId={c.ModbusId}" : $"{device.DisplayName}（{device.Code}）";
        var readOk = c.ReadStatus == ReadStatus.Success;
        // 讀取失敗時快照裡的數字是舊值或 0，拿去比範圍只會製造假警報。
        var usePrev = prev is not null && prev.ReadStatus == ReadStatus.Success;

        var fields = ChillerFields.Select(rule =>
        {
            var value = rule.Read(c);
            double? prevValue = usePrev ? rule.Read(prev!) : null;
            string level = LevelOk;
            string? note = null;
            if (!readOk)
            {
                level = LevelUnknown;
            }
            else if (IsOutOfRange(value, rule.Min, rule.Max))
            {
                level = LevelWarn;
                note = "超出暫定合理範圍";
            }
            else if (rule.MustNotDecrease && prevValue is not null && value < prevValue)
            {
                level = LevelWarn;
                note = "累計值比上一筆小（倒退）";
            }
            return new ChillerFieldValue(rule.Key, rule.Label, rule.Unit, value, prevValue, rule.Min, rule.Max, level, note);
        }).ToList();

        string chillerLevel;
        string detail;
        if (device is null && deviceMap is not null)
        {
            chillerLevel = LevelWarn;
            detail = "資料庫 device_chiller 沒有這個 ModbusId，資料不會寫入資料庫。";
        }
        else if (!readOk)
        {
            chillerLevel = LevelError;
            detail = $"讀取狀態＝{c.ReadStatus}，數值不可信。";
        }
        else if (LooksAllZero(c))
        {
            chillerLevel = LevelWarn;
            detail = "讀取成功但所有數值都是 0，常見原因是 Modbus 站號或暫存器位址對錯。";
        }
        else
        {
            var bad = fields.Where(f => f.Level == LevelWarn).ToList();
            chillerLevel = bad.Count > 0 ? LevelWarn : LevelOk;
            detail = bad.Count > 0
                ? $"{bad.Count} 個欄位需要人工確認：{string.Join("、", bad.Select(f => $"{f.Label}（{f.Note}）"))}"
                : $"{fields.Count} 個欄位都在暫定合理範圍內。";
        }

        checks.Add(new($"chiller-{c.ModbusId}", "數據內容", label, chillerLevel, detail));
        return new ChillerFieldReport(c.ModbusId, device?.Code, device?.DisplayName, chillerLevel, (int)c.ReadStatus, c.IsConnected, fields);
    }

    // ------------------------------------------------------------------
    // FCU 台數比對
    // ------------------------------------------------------------------

    private static FcuCoverage BuildCoverage(DdcSnapshot ddc, IReadOnlyList<DeviceFcu> allDevices, List<DiagnosticCheck> checks)
    {
        var mapped = allDevices.Where(d => d.Channel == ddc.Channel).ToList();
        var mappedKeys = mapped.ToDictionary(d => (d.StationId, d.Position));
        var receivedKeys = ddc.FcuList.Select(f => (f.StationId, f.Position)).ToHashSet();

        var unmapped = ddc.FcuList
            .Where(f => !mappedKeys.ContainsKey((f.StationId, f.Position)))
            .Select(f => $"站號 {f.StationId}／位置 {f.Position}（{f.Id}）").ToList();
        var missing = mapped
            .Where(d => !receivedKeys.Contains((d.StationId, d.Position)))
            .Select(d => $"站號 {d.StationId}／位置 {d.Position}（{d.ZoneCode ?? "未設分區"}）").ToList();
        var unresponsive = ddc.FcuList.Count(LooksUnresponsive);
        var tempOut = ddc.FcuList.Where(f => !LooksUnresponsive(f) && IsFcuTemperatureOutOfRange(f)).ToList();
        var matched = receivedKeys.Count(k => mappedKeys.ContainsKey(k));

        string level;
        var notes = new List<string>();
        if (ddc.ReadStatus != ReadStatus.Success)
        {
            level = LevelError;
            notes.Add($"讀取狀態＝{ddc.ReadStatus}，這一層 FCU 數值整層不可信。");
        }
        else
        {
            if (unmapped.Count > 0) notes.Add($"{unmapped.Count} 台現場有回報但資料庫沒有對照（不會寫入資料庫）");
            if (missing.Count > 0) notes.Add($"{missing.Count} 台資料庫有登記但現場沒有回報");
            if (unresponsive > 0) notes.Add($"{unresponsive} 台疑似沒回應（狀態全 Unknown 且溫度 0）");
            if (tempOut.Count > 0)
                notes.Add($"{tempOut.Count} 台溫度超出 {FcuTemperatureMin:0}~{FcuTemperatureMax:0}°C：" +
                          string.Join("、", tempOut.Take(5).Select(f => $"{f.Id} {f.Temperature:0.#}°C")));
            level = notes.Count > 0 ? LevelWarn : LevelOk;
        }

        checks.Add(new($"fcu-coverage-{(int)ddc.Channel}", "數據內容", $"{ChannelName(ddc.Channel)} FCU 台數與內容", level,
            notes.Count > 0
                ? string.Join("；", notes)
                : $"收到 {ddc.FcuList.Count} 台，全部對得上資料庫的 {mapped.Count} 台，數值都在範圍內。"));

        return new FcuCoverage(
            (int)ddc.Channel, ChannelName(ddc.Channel), level, ddc.FcuList.Count, mapped.Count, matched,
            unresponsive, tempOut.Count, unmapped.Take(ListLimit).ToList(), missing.Take(ListLimit).ToList());
    }

    // ------------------------------------------------------------------
    // 資料庫落地
    // ------------------------------------------------------------------

    private async Task<PersistenceStatus?> BuildPersistenceAsync(
        DateTimeOffset now, IngestPayload? latest, List<DiagnosticCheck> checks, CancellationToken ct)
    {
        var window = PersistenceRecentWindow;
        DiagnosticsRepository.PersistenceStatsRow stats;
        try
        {
            stats = await diagnostics.GetPersistenceStatsAsync(now.UtcDateTime - window, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "診斷頁讀取落地統計失敗");
            checks.Add(new("persistence", "資料庫", "資料庫落地", LevelError, $"查詢資料庫失敗：{ex.Message}"));
            return new PersistenceStatus(LevelError, (int)window.TotalMinutes, null, 0, 0, 0, null, 0, 0, 0, null, null, ex.Message);
        }

        var gatewayOk = latest is not null &&
                        (latest.Hanbell1.ReadStatus == ReadStatus.Success || latest.Hanbell2.ReadStatus == ReadStatus.Success);
        var ddcOk = latest is not null &&
                    (latest.Ddc1.ReadStatus == ReadStatus.Success || latest.Ddc2.ReadStatus == ReadStatus.Success);

        var chillerLevel = !gatewayOk ? LevelUnknown : stats.ChillerRecentRows == 0 ? LevelError : LevelOk;
        checks.Add(new("persistence-chiller", "資料庫", "冰水主機資料寫入 chiller_reading", chillerLevel,
            chillerLevel switch
            {
                LevelUnknown => "漢鐘通道目前沒有成功讀取，還無法判斷寫入是否正常。",
                LevelError => $"通道有讀到資料，但最近 {window.TotalMinutes:0} 分鐘資料庫沒有任何讀取成功的新列。檢查 collector log 是否有資料庫錯誤或「找不到 ModbusId」。{SimulationNote}",
                _ => $"最近 {window.TotalMinutes:0} 分鐘寫入 {stats.ChillerRecentRows} 筆讀取成功的資料，涵蓋 {stats.ChillerRecentDevices}/{stats.ChillerActiveDevices} 台。",
            }));

        var fcuLevel = !ddcOk ? LevelUnknown
            : stats.FcuRecentRows == 0 ? LevelError
            : stats.FcuRecentDevices < stats.FcuActiveDevices ? LevelWarn
            : LevelOk;
        checks.Add(new("persistence-fcu", "資料庫", "FCU 資料寫入 fcu_reading", fcuLevel,
            fcuLevel switch
            {
                LevelUnknown => "DDC 通道目前沒有成功讀取，還無法判斷寫入是否正常。",
                LevelError => $"通道有讀到資料，但最近 {window.TotalMinutes:0} 分鐘資料庫沒有任何讀取成功的新列。{SimulationNote}",
                _ => $"最近 {window.TotalMinutes:0} 分鐘寫入 {stats.FcuRecentRows} 筆讀取成功的資料，涵蓋 {stats.FcuRecentDevices}/{stats.FcuActiveDevices} 台" +
                     (fcuLevel == LevelWarn ? "——有設備沒有新資料，對照上方「資料庫有登記但現場沒有回報」清單。" : "。"),
            }));

        return new PersistenceStatus(
            Worst([chillerLevel, fcuLevel]), (int)window.TotalMinutes,
            stats.ChillerLatestTs?.AsUtcOffset(), stats.ChillerRecentRows, stats.ChillerRecentDevices, stats.ChillerActiveDevices,
            stats.FcuLatestTs?.AsUtcOffset(), stats.FcuRecentRows, stats.FcuRecentDevices, stats.FcuActiveDevices,
            stats.RollupChillerLatestBucket?.AsUtcOffset(), stats.RollupFcuLatestBucket?.AsUtcOffset(), null);
    }

    // ------------------------------------------------------------------
    // 排程
    // ------------------------------------------------------------------

    private IReadOnlyList<JobReport> BuildJobs(DateTimeOffset now, PersistenceStatus? persistence)
    {
        return jobs.Snapshot().Select(job =>
        {
            string level;
            if (job.LastRun is null) level = LevelUnknown;
            else if (job.LastRun.Succeeded == false) level = LevelError;
            // 超過預定時間 5 分鐘還沒開始下一輪，代表 BackgroundService 可能已經整個停掉。
            else if (job.NextRunAtUtc is not null && now > job.NextRunAtUtc.Value.AddMinutes(5)) level = LevelWarn;
            else level = LevelOk;

            string? evidenceLabel = null;
            DateTimeOffset? evidence = null;
            if (job.Key == RollupHostedService.JobKey && persistence is not null)
            {
                evidenceLabel = "資料庫最新聚合整點（FCU）";
                evidence = persistence.RollupFcuLatestBucketUtc ?? persistence.RollupChillerLatestBucketUtc;
                // 有原始資料進來，但聚合落後超過 3 小時——滾動視窗是 26 小時，正常最多只落後 1~2 個整點。
                var latestRaw = persistence.FcuLatestTsUtc ?? persistence.ChillerLatestTsUtc;
                if (level == LevelOk && latestRaw is not null && (latestRaw.Value - (evidence ?? DateTimeOffset.MinValue)).TotalHours > 3)
                {
                    level = LevelWarn;
                }
            }

            return new JobReport(
                job.Key, job.Name, level, (int)job.Interval.TotalSeconds, job.LastRun, job.NextRunAtUtc,
                evidenceLabel, evidence, job.History);
        }).ToList();
    }

    // ------------------------------------------------------------------
    // 連線變化歷史
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<ConnectionHistoryItem>> BuildHistoryAsync(CancellationToken ct)
    {
        var rows = await TryAsync(() => channelHealth.ListRecentAsync(30, ct), "讀取 channel_health");
        return rows?.Select(r => new ConnectionHistoryItem(
            r.Channel, ChannelName((Channel)r.Channel), r.ConnectionState, r.ReadStatus, r.ChangedAt.AsUtcOffset())).ToList() ?? [];
    }

    private async Task<T?> TryAsync<T>(Func<Task<T>> action, string what) where T : class
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "診斷頁{What}失敗", what);
            return null;
        }
    }
}
