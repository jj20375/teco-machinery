using System.Collections.Concurrent;
using System.Threading.Channels;
using CS_NS_Communication_Golf_DDC;
using CS_NS_Communication_Modbus;
using CS_NS_Teco_Golf_DataCollector;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Contracts = Teco.Hvac.Contracts;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Infrastructure.Repositories;

namespace Teco.Hvac.Collector;

/// <summary>
/// 整個系統的心臟：包住供應商 CS_TecoGolf_DataCollector，把它的 push 事件轉成
/// 內部資料流：即時推播（不節流）＋ 節流落地（DB）＋ 通道健康追蹤（看門狗）。
///
/// 生命週期規則（依說明書 §2–3、§7 訂）：
/// 1) 事件必須在 Start() 之前註冊，否則收不到首次 ConnectFailed。
/// 2) 連線路徑要整個 CS_ConnectPath 物件指派，改 getter 回傳的副本不會生效。
/// 3) 事件處理器內絕不做 I/O——只寫進 Channel&lt;T&gt; 就立刻返回，實際處理在獨立
///    consumer Task，避免拖慢供應商內部的輪詢執行緒（說明書 §7.1 明確警告）。
/// 4) 完全收不到任何事件超過門檻秒數，代表 Collector 可能卡死（DLL 無原始碼、
///    無法診斷內部狀態），由 supervisor loop 整個重建。
/// </summary>
public sealed class CollectorHostedService : BackgroundService
{
    private static readonly TimeZoneInfo TaipeiTimeZone = ResolveTaipeiTimeZone();

    private readonly CollectorOptions _options;
    private readonly ILogger<CollectorHostedService> _logger;
    private readonly DeviceRepository _deviceRepository;
    private readonly TimeSeriesWriter _timeSeriesWriter;
    private readonly ChannelWatchdog _watchdog;
    private readonly ThrottlePolicy _throttle;
    private readonly IngestHttpClient _ingestClient;

    private readonly Channel<EventArgsDataReceived> _dataChannel = Channel.CreateBounded<EventArgsDataReceived>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<EventArgsConnectStatus> _connectionChannel = Channel.CreateBounded<EventArgsConnectStatus>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly ConcurrentDictionary<(Contracts.Channel, byte, int), bool> _warnedUnmappedFcu = new();

    private IReadOnlyDictionary<int, DeviceChiller> _chillersByModbusId = new Dictionary<int, DeviceChiller>();
    private IReadOnlyDictionary<(Contracts.Channel, byte, int), (int Id, string Floor)> _fcuIdMap =
        new Dictionary<(Contracts.Channel, byte, int), (int, string)>();

    private readonly AlarmEngine _alarmEngine;

    public CollectorHostedService(
        IOptions<CollectorOptions> options,
        ILogger<CollectorHostedService> logger,
        DeviceRepository deviceRepository,
        TimeSeriesWriter timeSeriesWriter,
        ChannelWatchdog watchdog,
        ThrottlePolicy throttle,
        IngestHttpClient ingestClient,
        AlarmEngine alarmEngine)
    {
        _options = options.Value;
        _logger = logger;
        _deviceRepository = deviceRepository;
        _timeSeriesWriter = timeSeriesWriter;
        _watchdog = watchdog;
        _throttle = throttle;
        _ingestClient = ingestClient;
        _alarmEngine = alarmEngine;
    }

    /// <summary>供 /healthz 使用。</summary>
    public ChannelWatchdog Watchdog => _watchdog;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadDeviceMappingsAsync(stoppingToken);

        var dataConsumer = ConsumeDataEventsAsync(stoppingToken);
        var connectionConsumer = ConsumeConnectionEventsAsync(stoppingToken);
        var supervisor = RunSupervisorLoopAsync(stoppingToken);

        await Task.WhenAll(dataConsumer, connectionConsumer, supervisor);
    }

    private async Task LoadDeviceMappingsAsync(CancellationToken ct)
    {
        try
        {
            _chillersByModbusId = await _deviceRepository.GetChillersByModbusIdAsync(ct);
            _fcuIdMap = await _deviceRepository.GetFcuIdMapAsync(ct);
            await _alarmEngine.LoadRulesAsync(ct);
            _logger.LogInformation(
                "設備對照表載入完成：{ChillerCount} 台冰水主機、{FcuCount} 台 FCU",
                _chillersByModbusId.Count, _fcuIdMap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入設備對照表失敗，資料庫落地將被跳過直到下次重建；即時推播不受影響");
        }
    }

    // ------------------------------------------------------------------
    // Supervisor：管理 CS_TecoGolf_DataCollector 的生命週期與自我重建
    // ------------------------------------------------------------------

    private async Task RunSupervisorLoopAsync(CancellationToken ct)
    {
        int restartCount = 0;
        while (!ct.IsCancellationRequested)
        {
            using var collector = CreateAndStartCollector();
            _logger.LogInformation("Collector 已啟動（第 {Count} 次）", restartCount + 1);

            try
            {
                var ticksSinceRuleReload = 0;
                while (!ct.IsCancellationRequested && !_watchdog.ShouldRestart(DateTimeOffset.UtcNow))
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);

                    // 每 4 個迴圈（約 1 分鐘）重新讀一次告警規則，「告警門檻設定」畫面改門檻後
                    // 不用重建 Collector 就會生效——原本規則只在啟動時載入一次，是先前的已知缺口。
                    // 放這裡而不是獨立的 Timer，是因為這個迴圈本來就在跑、生命週期已經跟著
                    // Collector 走，不需要再多管一個計時器的啟動/釋放。
                    if (++ticksSinceRuleReload >= 4)
                    {
                        ticksSinceRuleReload = 0;
                        try
                        {
                            await _alarmEngine.LoadRulesAsync(ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "定期重新載入告警規則失敗，沿用目前規則直到下次重試");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止流程
            }
            finally
            {
                collector.EventDataReceived -= OnEventDataReceived;
                collector.EventConnectionStatusChanged -= OnEventConnectionStatusChanged;
                collector.Stop();
            }

            if (ct.IsCancellationRequested) break;

            restartCount++;
            _logger.LogWarning(
                "超過 {Seconds} 秒未收到任何事件，重建 Collector（累計第 {Count} 次）",
                _options.WatchdogRestartAfterSeconds, restartCount);
        }
    }

    private CS_TecoGolf_DataCollector CreateAndStartCollector()
    {
        var collector = new CS_TecoGolf_DataCollector();

        // 先註冊事件，才能收到 Start 之後的首次 ConnectFailed（說明書 §3.1 明確要求順序）。
        collector.EventConnectionStatusChanged += OnEventConnectionStatusChanged;
        collector.EventDataReceived += OnEventDataReceived;

        var hanbellPath = new CS_ConnectPath(_options.Hanbell.Ip, _options.Hanbell.Port)
        {
            Externed = _options.Hanbell.BaudRate,
        };
        collector.HanbellConnectPath = hanbellPath;
        collector.DDC1ConnectPath = new CS_ConnectPath(_options.Ddc1.Ip, _options.Ddc1.Port);
        collector.DDC2ConnectPath = new CS_ConnectPath(_options.Ddc2.Ip, _options.Ddc2.Port);

        collector.Start();
        return collector;
    }

    // 事件處理器：絕不做 I/O，只丟進 Channel 立刻返回（說明書 §7.1）。
    private void OnEventDataReceived(object? sender, EventArgsDataReceived e) =>
        _dataChannel.Writer.TryWrite(e);

    private void OnEventConnectionStatusChanged(object? sender, EventArgsConnectStatus e) =>
        _connectionChannel.Writer.TryWrite(e);

    // ------------------------------------------------------------------
    // 資料事件消費：映射 → 即時推播（不節流）→ 節流落地（DB）
    // ------------------------------------------------------------------

    private async Task ConsumeDataEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _dataChannel.Reader.ReadAllAsync(ct))
            {
                await ProcessDataEventAsync(e, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止流程
        }
    }

    private async Task ProcessDataEventAsync(EventArgsDataReceived e, CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var updateTimeUtc = ToUtc(e.UpdateTime);

        var h1 = e.HanbellStatus1.ToContract(modbusId: 1, updateTimeUtc);
        var h2 = e.HanbellStatus2.ToContract(modbusId: 2, updateTimeUtc);
        var ddc1 = e.DDC1Status.ToContract(Contracts.Channel.Ddc1, updateTimeUtc);
        var ddc2 = e.DDC2Status.ToContract(Contracts.Channel.Ddc2, updateTimeUtc);

        // 看門狗記帳：兩台漢鐘共用同一條 Gateway TCP，任一台讀取成功就視為該通道本輪健康。
        var gatewayReadStatus = (h1.ReadStatus == Contracts.ReadStatus.Success || h2.ReadStatus == Contracts.ReadStatus.Success)
            ? Contracts.ReadStatus.Success
            : (h1.ReadStatus == Contracts.ReadStatus.Failed || h2.ReadStatus == Contracts.ReadStatus.Failed
                ? Contracts.ReadStatus.Failed
                : Contracts.ReadStatus.Disconnected);
        _watchdog.OnDataReceived(Contracts.Channel.HanbellModbusGateway, gatewayReadStatus, nowUtc);
        _watchdog.OnDataReceived(Contracts.Channel.Ddc1, ddc1.ReadStatus, nowUtc);
        _watchdog.OnDataReceived(Contracts.Channel.Ddc2, ddc2.ReadStatus, nowUtc);

        // 即時推播：每次事件都送，不節流（前台看板需要 5 秒一次的鮮度）。
        var payload = new Contracts.IngestPayload
        {
            UpdateTimeUtc = updateTimeUtc,
            ReceivedAtUtc = nowUtc,
            Hanbell1 = h1,
            Hanbell2 = h2,
            Ddc1 = ddc1,
            Ddc2 = ddc2,
        };
        await _ingestClient.PostDataAsync(payload, ct);

        // 告警評估：不受節流影響，每次事件都評估（告警要即時反應，不能等 60 秒落地週期）。
        await EvaluateAlarmsAsync(h1, h2, ddc1, ddc2, nowUtc, ct);

        // 節流落地：只有決定要寫的才進資料庫。
        await WriteChillerIfDueAsync(h1, nowUtc, ct);
        await WriteChillerIfDueAsync(h2, nowUtc, ct);
        await WriteFcuListIfDueAsync(ddc1, nowUtc, ct);
        await WriteFcuListIfDueAsync(ddc2, nowUtc, ct);
    }

    private async Task EvaluateAlarmsAsync(
        Contracts.ChillerSnapshot h1, Contracts.ChillerSnapshot h2,
        Contracts.DdcSnapshot ddc1, Contracts.DdcSnapshot ddc2, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (_chillersByModbusId.TryGetValue(h1.ModbusId, out var c1))
            await _alarmEngine.EvaluateChillerAsync(c1.Id, h1, nowUtc, ct);
        if (_chillersByModbusId.TryGetValue(h2.ModbusId, out var c2))
            await _alarmEngine.EvaluateChillerAsync(c2.Id, h2, nowUtc, ct);

        foreach (var fcu in ddc1.FcuList.Concat(ddc2.FcuList))
        {
            if (_fcuIdMap.TryGetValue((fcu.Channel, fcu.StationId, fcu.Position), out var mapped))
            {
                var ddcReadStatus = fcu.Channel == Contracts.Channel.Ddc1 ? ddc1.ReadStatus : ddc2.ReadStatus;
                await _alarmEngine.EvaluateFcuAsync(mapped.Id, mapped.Floor, fcu, ddcReadStatus, nowUtc, ct);
            }
        }
    }

    private async Task WriteChillerIfDueAsync(Contracts.ChillerSnapshot snapshot, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (!_chillersByModbusId.TryGetValue(snapshot.ModbusId, out var device))
        {
            _logger.LogWarning("找不到 ModbusId={ModbusId} 對應的 device_chiller，略過落地", snapshot.ModbusId);
            return;
        }
        // 讀取不成功時快照裡是 SDK 保留的舊值或初始 0，不是這個時間點的量測值，寫進去只會讓時序表
        // 混入每天十幾萬筆假資料。斷線期間的紀錄已經在 channel_health。要放在節流判斷之前，
        // 否則恢復連線後第一筆成功讀值會被「剛寫過」的節流擋掉。
        if (snapshot.ReadStatus != Contracts.ReadStatus.Success) return;
        if (!_throttle.ShouldWriteChiller(snapshot.ModbusId, nowUtc)) return;

        await _timeSeriesWriter.WriteChillerReadingsAsync(
            [(device.Id, nowUtc, snapshot)], ct);
    }

    private async Task WriteFcuListIfDueAsync(Contracts.DdcSnapshot ddc, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // 理由同 WriteChillerIfDueAsync。DDC 是整批輪詢：Failed 時清單混著新值、舊值與初始值，
        // 又沒有逐台成功旗標（說明書表 11），分不出哪幾台可信，所以整批不寫。
        if (ddc.ReadStatus != Contracts.ReadStatus.Success) return;

        List<(int DeviceId, DateTimeOffset TsUtc, Contracts.FcuSnapshot Snapshot, Contracts.ReadStatus ReadStatus)>? rows = null;

        foreach (var fcu in ddc.FcuList)
        {
            var key = (fcu.Channel, fcu.StationId, fcu.Position);
            if (!_fcuIdMap.TryGetValue(key, out var mapped))
            {
                if (_warnedUnmappedFcu.TryAdd(key, true))
                {
                    _logger.LogWarning(
                        "找不到 Channel={Channel} StationId={StationId} Position={Position} 對應的 device_fcu，略過落地（只警告一次）",
                        fcu.Channel, fcu.StationId, fcu.Position);
                }
                continue;
            }
            if (!_throttle.ShouldWriteFcu(fcu, nowUtc)) continue;

            (rows ??= []).Add((mapped.Id, ddc.UpdateTimeUtc, fcu, ddc.ReadStatus));
        }

        if (rows is { Count: > 0 })
        {
            await _timeSeriesWriter.WriteFcuReadingsAsync(rows, ct);
        }
    }

    // ------------------------------------------------------------------
    // 連線事件消費
    // ------------------------------------------------------------------

    private async Task ConsumeConnectionEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _connectionChannel.Reader.ReadAllAsync(ct))
            {
                var channel = e.ConnectionChannel.ToContractChannel();
                var state = e.ConnectionState.ToContract();
                var triggerTimeUtc = ToUtc(e.TriggerTime);

                await _watchdog.OnConnectionStatusChangedAsync(channel, state, triggerTimeUtc, ct);

                var payload = new Contracts.ConnectionStatusPayload
                {
                    Channel = channel,
                    Ip = e.ConnectPath.ID,
                    Port = e.ConnectPath.Path,
                    ConnectionState = state,
                    IsConnected = e.IsConnected,
                    TriggerTimeUtc = triggerTimeUtc,
                };
                await _ingestClient.PostConnectionStatusAsync(payload, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止流程
        }
    }

    // ------------------------------------------------------------------
    // 時間轉換：UpdateTime/TriggerTime 是 DateTime.Now（本機時間、無時區資訊），
    // 不依賴容器 TZ 環境變數，明確以 Asia/Taipei 解讀後轉 UTC，避免時區設定漂移。
    // ------------------------------------------------------------------

    private static DateTimeOffset ToUtc(DateTime localNaive)
    {
        var unspecified = DateTime.SpecifyKind(localNaive, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, TaipeiTimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static TimeZoneInfo ResolveTaipeiTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows 容器/精簡映像可能沒有 IANA 資料庫，退回固定 UTC+8（台灣無日光節約時間，固定偏移是安全的）。
            return TimeZoneInfo.CreateCustomTimeZone("Asia/Taipei-Fixed", TimeSpan.FromHours(8), "Taipei (fixed)", "Taipei (fixed)");
        }
    }
}
