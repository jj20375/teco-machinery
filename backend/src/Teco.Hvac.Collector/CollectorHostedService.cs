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
/// 東元智慧空調採集核心託管服務 (CollectorHostedService)。
/// 
/// 【系統架構與職責】
/// 本服務為整個監控系統的心臟，負責封裝設備供應商所提供的 <see cref="CS_TecoGolf_DataCollector"/> 硬體採集 DLL，
/// 將硬體輪詢所產生的 push 事件安全轉發為內部三軌資料流：
/// 1. 即時推播軌（Realtime Broadcast）：不經節流，每次收到硬體事件即刻轉發至 Web API，供 SignalR 前台看板呈現 5 秒更新的即時動態。
/// 2. 節流落地軌（Throttled Persistence）：依據節流策略過濾無變化或未達週期之數據，批次寫入 MariaDB 時序資料表，防止資料庫爆量。
/// 3. 通道看門狗軌（Channel Watchdog）：即時追蹤各通訊通道健康狀態、連線區段變更及連線中斷告警。
///
/// 【供應商 DLL 核心生命週期規則（依原廠說明書 §2–3、§7 規範）】
/// 1. 事件註冊順序：事件處理常式必須在呼叫 <see cref="CS_TecoGolf_DataCollector.Start"/> 之前完成註冊，否則將遺失首次啟動時的 ConnectFailed 事件。
/// 2. 連線路徑賦值：設定 IP/Port 時必須建立並指派全新的 <see cref="CS_ConnectPath"/> 物件實例；直接修改其 getter 所回傳之副本屬性將無法生效。
/// 3. 事件無阻塞原則：事件處理回呼常式內部絕對不可執行任何阻塞性或非同步 I/O 操作；僅允許將事件物件壓入記憶體通道 (<see cref="Channel{T}"/>) 即刻返回，
///    實際處理交由獨立消費者 Task 執行，嚴格防範拖慢供應商內部的底層輪詢執行緒（說明書 §7.1 明確警示）。
/// 4. 自我重構與容器重啟：若連續超過設定門檻秒數完全未收到任何資料事件，判定供應商 DLL 內部執行緒卡死（DLL 無原始碼無法診斷其內部狀態），
///    由監督迴圈執行自我重建；若多次重建仍無效，則終止行程由外層 Docker 容器進行乾淨重啟。
/// 5. 連線狀態定時重送：供應商 DLL 僅在連線狀態「發生變更」時拋出事件，若 Web API 晚於 Collector 啟動或獨立重啟，會遺失記憶體狀態；
///    因此監督迴圈每隔 15 秒會將各通道最新快照重送至 API，確保畫面連線狀態即時校正。
/// </summary>
public sealed class CollectorHostedService : BackgroundService
{
    /// <summary>
    /// 台北標準時區物件 (Asia/Taipei, UTC+8)。
    /// 供應商 DLL 回傳的 UpdateTime 為本機未指定時區時間 (<see cref="DateTimeKind.Unspecified"/>)，
    /// 透過明確的時區物件將其轉換為 UTC，避免受主機或容器環境變數影響而發生時間漂移。
    /// </summary>
    private static readonly TimeZoneInfo TaipeiTimeZone = ResolveTaipeiTimeZone();

    /// <summary>
    /// 採集器整體組態設定選項，包含各通訊通道連線 IP/Port、節流秒數、看門狗逾時門檻等。
    /// </summary>
    private readonly CollectorOptions _options;

    /// <summary>
    /// 日誌紀錄器實例，用於輸出生命週期、設備映射、連線變化及例外訊息。
    /// </summary>
    private readonly ILogger<CollectorHostedService> _logger;

    /// <summary>
    /// 設備資料存取倉儲，負責從資料庫載入冰水主機與 FCU 設備的主鍵及通訊映射資訊。
    /// </summary>
    private readonly DeviceRepository _deviceRepository;

    /// <summary>
    /// 時序資料寫入器，負責將通過有效性驗證與節流規則的設備遙測數據批次寫入 MariaDB。
    /// </summary>
    private readonly TimeSeriesWriter _timeSeriesWriter;

    /// <summary>
    /// 通道看門狗實例，負責維護通訊通道健康度、最後通訊時間以及判定是否需要觸發重建。
    /// </summary>
    private readonly ChannelWatchdog _watchdog;

    /// <summary>
    /// 時序數據落地節流控制器，控制冰水主機與 FCU 遙測資料落地至資料庫的頻率與變化門檻。
    /// </summary>
    private readonly ThrottlePolicy _throttle;

    /// <summary>
    /// 內部 HTTP 攝取客戶端，負責將即時遙測數據與連線狀態推播至 Web API 的內部端點。
    /// </summary>
    private readonly IngestHttpClient _ingestClient;

    /// <summary>
    /// 主機應用程式生命週期管理員，用於在自我重建失敗時終止應用程式以觸發 Docker 容器重啟。
    /// </summary>
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// 遙測數據事件的有界緩衝佇列 (<see cref="Channel{T}"/>)。
    /// 容量設定為 16，滿載時自動丟棄最舊事件 (<see cref="BoundedChannelFullMode.DropOldest"/>)，確保生產端零阻塞。
    /// </summary>
    private readonly Channel<EventArgsDataReceived> _dataChannel = Channel.CreateBounded<EventArgsDataReceived>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// 連線狀態變更事件的有界緩衝佇列 (<see cref="Channel{T}"/>)。
    /// 容量設定為 32，滿載時自動丟棄最舊事件，避免突發網路抖動造成佇列無限制膨脹。
    /// </summary>
    private readonly Channel<EventArgsConnectStatus> _connectionChannel = Channel.CreateBounded<EventArgsConnectStatus>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// 記錄已發出未映射警告的 FCU 複合識別鍵字典，鍵值為 (通道, 站號, 位置)。
    /// 用於確保未知設備只在日誌中警告一次，防止每 5 秒重複洗版日誌。
    /// </summary>
    private readonly ConcurrentDictionary<(Contracts.Channel, byte, int), bool> _warnedUnmappedFcu = new();

    /// <summary>
    /// 各實體通訊通道最新的連線狀態酬載快照字典。
    /// 供監督迴圈每隔 15 秒重新推送至 Web API，避免 API 重啟後因缺乏事件而使狀態卡在離線。
    /// </summary>
    private readonly ConcurrentDictionary<Contracts.Channel, Contracts.ConnectionStatusPayload> _lastConnection = new();

    /// <summary>
    /// 冰水主機 Modbus ID 對照快取字典，鍵值為 Modbus 站號 (1 或 2)，值為冰水主機實體物件。
    /// </summary>
    private IReadOnlyDictionary<int, DeviceChiller> _chillersByModbusId = new Dictionary<int, DeviceChiller>();

    /// <summary>
    /// FCU 設備複合識別鍵對照快取字典，鍵值為 (通道, 站號, 內部位置)，值為 (資料庫設備ID, 所屬樓層)。
    /// </summary>
    private IReadOnlyDictionary<(Contracts.Channel, byte, int), (int Id, string Floor)> _fcuIdMap =
        new Dictionary<(Contracts.Channel, byte, int), (int, string)>();

    /// <summary>
    /// 告警評估引擎，負責在接收到遙測數據時即時比對硬體故障旗標與動態告警門檻規則。
    /// </summary>
    private readonly AlarmEngine _alarmEngine;

    /// <summary>
    /// 初始化 <see cref="CollectorHostedService"/> 的新執行個體，透過相依性注入注入各項必要服務。
    /// </summary>
    /// <param name="options">採集器組態選項包裝物件，包含網路位址、節流週期與看門狗門檻。</param>
    /// <param name="logger">日誌紀錄器實例，用於追蹤服務運作過程與錯誤記錄。</param>
    /// <param name="deviceRepository">設備資料倉儲，用於查詢冰機與 FCU 的設定與映射關係。</param>
    /// <param name="timeSeriesWriter">時序資料批次寫入器，負責時序點位的 SQL 高速落地。</param>
    /// <param name="watchdog">通訊看門狗實例，維護通道健康狀態與逾時重啟決策。</param>
    /// <param name="throttle">時序落地節流規則處理器，判定快照是否符合落地條件。</param>
    /// <param name="ingestClient">內部 API 通訊客戶端，傳遞即時推播與連線通知。</param>
    /// <param name="alarmEngine">告警計算引擎，評估設備即時數值並開立或解除告警。</param>
    /// <param name="lifetime">應用程式生命週期管理實例，用於觸發處理程序重啟退出。</param>
    public CollectorHostedService(
        IOptions<CollectorOptions> options,
        ILogger<CollectorHostedService> logger,
        DeviceRepository deviceRepository,
        TimeSeriesWriter timeSeriesWriter,
        ChannelWatchdog watchdog,
        ThrottlePolicy throttle,
        IngestHttpClient ingestClient,
        AlarmEngine alarmEngine,
        IHostApplicationLifetime lifetime)
    {
        _options = options.Value;
        _logger = logger;
        _deviceRepository = deviceRepository;
        _timeSeriesWriter = timeSeriesWriter;
        _watchdog = watchdog;
        _throttle = throttle;
        _ingestClient = ingestClient;
        _alarmEngine = alarmEngine;
        _lifetime = lifetime;
    }

    /// <summary>
    /// 取得目前的通訊通道看門狗實例，供 Web API 的健康檢查端點 (/healthz) 讀取整體健康狀態。
    /// </summary>
    public ChannelWatchdog Watchdog => _watchdog;

    /// <summary>
    /// 背景服務的主要執行入口點。在服務啟動時載入設備映射，並並行執行資料消費、連線消費與監督管理三項任務。
    /// </summary>
    /// <param name="stoppingToken">通知背景服務應該停止運作的取消語彙基元。</param>
    /// <returns>代表非同步執行作業的 <see cref="Task"/>。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. 啟動初期先從資料庫快取設備映射與告警規則
        await LoadDeviceMappingsAsync(stoppingToken);

        // 2. 啟動遙測數據事件消費者工作任務
        var dataConsumer = ConsumeDataEventsAsync(stoppingToken);

        // 3. 啟動連線狀態事件消費者工作任務
        var connectionConsumer = ConsumeConnectionEventsAsync(stoppingToken);

        // 4. 啟動採集核心生命週期監督迴圈（管理 DLL 建立、逾時重構、定時重送連線狀態）
        var supervisor = RunSupervisorLoopAsync(stoppingToken);

        // 5. 等待所有常駐工作任務完成（通常在 stoppingToken 觸發取消時結束）
        await Task.WhenAll(dataConsumer, connectionConsumer, supervisor);
    }

    /// <summary>
    /// 從資料庫非同步載入冰水主機對照表、FCU 對照表以及啟用的告警規則至記憶體快取。
    /// 若載入失敗則記錄錯誤並維持空對照表，跳過時序資料庫落地，但即時推播與看門狗不受影響。
    /// </summary>
    /// <param name="ct">控制非同步取消的語彙基元。</param>
    /// <returns>代表非同步載入作業的 <see cref="Task"/>。</returns>
    private async Task LoadDeviceMappingsAsync(CancellationToken ct)
    {
        try
        {
            // 讀取冰水主機 Modbus ID 對照
            _chillersByModbusId = await _deviceRepository.GetChillersByModbusIdAsync(ct);

            // 讀取 FCU (Channel, StationId, Position) 複合鍵對照
            _fcuIdMap = await _deviceRepository.GetFcuIdMapAsync(ct);

            // 讀取後台設定之告警門檻規則
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

    /// <summary>
    /// 執行生命週期監督迴圈。
    /// 負責維護供應商 DLL 實例的運作、定期重播連線狀態快照給 API，
    /// 並在看門狗回報資料事件超時時執行自我重構；若多次重構無效則主動結束程序交由 Docker 容器重啟。
    /// </summary>
    /// <param name="ct">控制監督迴圈停止的取消語彙基元。</param>
    /// <returns>代表非同步監督迴圈的 <see cref="Task"/>。</returns>
    private async Task RunSupervisorLoopAsync(CancellationToken ct)
    {
        // 累計內部重建次數計數器
        int restartCount = 0;

        // 連續未收到任何資料事件的逾時次數計數器
        int consecutiveTimeouts = 0;

        while (!ct.IsCancellationRequested)
        {
            // 記錄本次重建啟動的時間點
            var startedAtUtc = DateTimeOffset.UtcNow;

            // 重設看門狗計時基準點，避免延續上一次逾時導致立即判定重啟
            _watchdog.ResetEventClock(startedAtUtc);

            // 建立並啟動供應商採集核心物件（使用 using 確保舊實例於結束時徹底釋放）
            using var collector = CreateAndStartCollector();
            _logger.LogInformation("Collector 已啟動（第 {Count} 次）", restartCount + 1);

            try
            {
                // 當未收到停止信號且看門狗未回報超時時，維持心跳巡檢
                while (!ct.IsCancellationRequested && !_watchdog.ShouldRestart(DateTimeOffset.UtcNow))
                {
                    // 每 15 秒執行一次心跳檢查與連線狀態重播
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);

                    // 將各通道最後一次已知的連線狀態重送給 API，避免 API 重啟後連線狀態遺失
                    foreach (var status in _lastConnection.Values)
                        await _ingestClient.PostConnectionStatusAsync(status, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // 收到服務停止請求，正常跳出迴圈
            }
            finally
            {
                // 解除事件訂閱並停止採集核心，確保資源釋放
                collector.EventDataReceived -= OnEventDataReceived;
                collector.EventConnectionStatusChanged -= OnEventConnectionStatusChanged;
                collector.Stop();
            }

            // 若為正常關機程序則直接離開監督迴圈
            if (ct.IsCancellationRequested) break;

            restartCount++;

            // 判定在本次採集週期中是否曾收到過資料事件：
            // 若曾收到過事件，代表先前重建有效，連續逾時次數重設為 1；若完全未曾收到事件，則累加連續逾時次數。
            consecutiveTimeouts = _watchdog.LastDataEventUtc > startedAtUtc ? 1 : consecutiveTimeouts + 1;

            // 若連續逾時次數達到設定門檻（預設 2 次），代表內部重構已無法恢復底層通訊，必須結束程序由 Docker 重啟容器
            if (consecutiveTimeouts >= Math.Max(1, _options.WatchdogExitAfterConsecutiveTimeouts))
            {
                _logger.LogCritical(
                    "連續 {Count} 次共超過 {Seconds} 秒收不到任何資料事件，重建 Collector 無效，結束程序交給 Docker 重啟",
                    consecutiveTimeouts, _options.WatchdogRestartAfterSeconds);
                Environment.ExitCode = 1;
                _lifetime.StopApplication();
                break;
            }

            _logger.LogWarning(
                "超過 {Seconds} 秒未收到任何資料事件，重建 Collector（累計第 {Count} 次，連續第 {Consecutive} 次）",
                _options.WatchdogRestartAfterSeconds, restartCount, consecutiveTimeouts);
        }
    }

    /// <summary>
    /// 工廠方法：建立並初始化全新的供應商 <see cref="CS_TecoGolf_DataCollector"/> 實例，
    /// 依原廠規格手冊順序綁定事件回呼、配置連線路徑並啟動內部輪詢。
    /// </summary>
    /// <returns>已完成設定並啟動的 <see cref="CS_TecoGolf_DataCollector"/> 實例。</returns>
    private CS_TecoGolf_DataCollector CreateAndStartCollector()
    {
        var collector = new CS_TecoGolf_DataCollector();

        // 【規則 1】必須在呼叫 Start 之前註冊事件，才能收到啟動後的首次 ConnectFailed 事件（原廠說明書 §3.1）
        collector.EventConnectionStatusChanged += OnEventConnectionStatusChanged;
        collector.EventDataReceived += OnEventDataReceived;

        // 【規則 2】指派連線路徑時必須賦予全新的 CS_ConnectPath 物件實例（原廠說明書 §2.2）
        var hanbellPath = new CS_ConnectPath(_options.Hanbell.Ip, _options.Hanbell.Port)
        {
            Externed = _options.Hanbell.BaudRate,
        };
        collector.HanbellConnectPath = hanbellPath;
        collector.DDC1ConnectPath = new CS_ConnectPath(_options.Ddc1.Ip, _options.Ddc1.Port);
        collector.DDC2ConnectPath = new CS_ConnectPath(_options.Ddc2.Ip, _options.Ddc2.Port);

        // 啟動硬體輪詢線程
        collector.Start();
        return collector;
    }

    /// <summary>
    /// 供應商資料接收事件回呼處理常式。
    /// 【規則 3】此方法由供應商底層輪詢線程直接觸發，絕對禁止執行 I/O，僅寫入記憶體 Channel 即刻返回。
    /// </summary>
    /// <param name="sender">事件引發來源物件。</param>
    /// <param name="e">包含漢鐘冰機 1/2 與 DDC 1/2 採集數據的事件參數物件。</param>
    private void OnEventDataReceived(object? sender, EventArgsDataReceived e)
    {
        // 供除錯測試看門狗自我重建使用，若開啟則直接丟棄所有事件
        if (_options.DebugDropDataEvents) return;

        // 非同步無鎖寫入佇列，若佇列已滿依策略丟棄最舊資料，絕不拖慢生產端
        _dataChannel.Writer.TryWrite(e);
    }

    /// <summary>
    /// 供應商連線狀態變更事件回呼處理常式。
    /// 僅將狀態變更事件寫入連線佇列，絕不執行任何阻塞作業。
    /// </summary>
    /// <param name="sender">事件引發來源物件。</param>
    /// <param name="e">包含通道識別、連線狀態與觸發時間的事件參數物件。</param>
    private void OnEventConnectionStatusChanged(object? sender, EventArgsConnectStatus e) =>
        _connectionChannel.Writer.TryWrite(e);

    // ------------------------------------------------------------------
    // 資料事件消費：映射 → 即時推播（不節流）→ 節流落地（DB）
    // ------------------------------------------------------------------

    /// <summary>
    /// 非同步資料事件消費者背景任務。持續自 <see cref="_dataChannel"/> 讀取事件並轉發至完整處理管道。
    /// </summary>
    /// <param name="ct">控制消費者停止的取消語彙基元。</param>
    /// <returns>代表非同步消費作業的 <see cref="Task"/>。</returns>
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
            // 正常停止作業流程，忽略取消例外
        }
    }

    /// <summary>
    /// 處理單筆由供應商發出的完整資料事件：
    /// 進行時區轉換、合約 DTO 轉換、看門狗計帳、即時推播、即時告警評估與時序節流落地。
    /// </summary>
    /// <param name="e">由供應商回報之原生事件數據。</param>
    /// <param name="ct">控制非同步處理作業的取消語彙基元。</param>
    /// <returns>代表非同步處理作業的 <see cref="Task"/>。</returns>
    private async Task ProcessDataEventAsync(EventArgsDataReceived e, CancellationToken ct)
    {
        // 取得目前處理的 UTC 時間戳記
        var nowUtc = DateTimeOffset.UtcNow;

        // 將供應商事件中無時區的 UpdateTime 解析為標準台北時區並轉為 UTC
        var updateTimeUtc = ToUtc(e.UpdateTime);

        // 映射並轉換為 Contracts 定義的領域快照 DTO
        var h1 = e.HanbellStatus1.ToContract(modbusId: 1, updateTimeUtc);
        var h2 = e.HanbellStatus2.ToContract(modbusId: 2, updateTimeUtc);
        var ddc1 = e.DDC1Status.ToContract(Contracts.Channel.Ddc1, updateTimeUtc);
        var ddc2 = e.DDC2Status.ToContract(Contracts.Channel.Ddc2, updateTimeUtc);

        // 【看門狗計帳】漢鐘 1 號機與 2 號機共用同一個 Modbus 閘道 TCP 通訊路徑：
        // 只要任一台讀取成功即判定此閘道通道本輪通訊正常；兩台皆失敗才判斷為失敗。
        var gatewayReadStatus = (h1.ReadStatus == Contracts.ReadStatus.Success || h2.ReadStatus == Contracts.ReadStatus.Success)
            ? Contracts.ReadStatus.Success
            : (h1.ReadStatus == Contracts.ReadStatus.Failed || h2.ReadStatus == Contracts.ReadStatus.Failed
                ? Contracts.ReadStatus.Failed
                : Contracts.ReadStatus.Disconnected);

        _watchdog.OnDataReceived(Contracts.Channel.HanbellModbusGateway, gatewayReadStatus, nowUtc);
        _watchdog.OnDataReceived(Contracts.Channel.Ddc1, ddc1.ReadStatus, nowUtc);
        _watchdog.OnDataReceived(Contracts.Channel.Ddc2, ddc2.ReadStatus, nowUtc);

        // 【第一軌：即時推播】每次收到事件皆即刻送出，不套用節流，滿足前台監控儀表板 5 秒更新之鮮度需求
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

        // 【第二軌：即時告警評估】每次事件到達均立即評估，不等待 60 秒節流週期，確保異常發生時零延遲通報
        await EvaluateAlarmsAsync(h1, h2, ddc1, ddc2, gatewayReadStatus, nowUtc, ct);

        // 【第三軌：節流落地】檢查時序資料是否符合落地週期或突變條件，僅在必要時寫入 MariaDB
        await WriteChillerIfDueAsync(h1, nowUtc, ct);
        await WriteChillerIfDueAsync(h2, nowUtc, ct);
        await WriteFcuListIfDueAsync(ddc1, nowUtc, ct);
        await WriteFcuListIfDueAsync(ddc2, nowUtc, ct);
    }

    /// <summary>
    /// 執行全面的即時告警評估：包含檢查規則指紋是否異動、通道通訊離線告警、漢鐘硬體旗標與門檻告警，以及 FCU 溫度門檻告警。
    /// </summary>
    /// <param name="h1">漢鐘 1 號機即時快照。</param>
    /// <param name="h2">漢鐘 2 號機即時快照。</param>
    /// <param name="ddc1">DDC1 (B1 樓層) 即時快照。</param>
    /// <param name="ddc2">DDC2 (B2 樓層) 即時快照。</param>
    /// <param name="gatewayReadStatus">漢鐘 Modbus 閘道綜合讀取狀態。</param>
    /// <param name="nowUtc">當前計算使用的標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步作業的取消語彙基元。</param>
    /// <returns>代表非同步告警評估作業的 <see cref="Task"/>。</returns>
    private async Task EvaluateAlarmsAsync(
        Contracts.ChillerSnapshot h1, Contracts.ChillerSnapshot h2,
        Contracts.DdcSnapshot ddc1, Contracts.DdcSnapshot ddc2, Contracts.ReadStatus gatewayReadStatus,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        // 若後台有修改告警規則，透過資料庫指紋即刻熱重載規則快取
        await _alarmEngine.RefreshRulesIfChangedAsync(ct);

        // 1. 評估各通道連線離線告警（通道離線本身即為關鍵事件，不受單機讀取失敗限制）
        await _alarmEngine.EvaluateChannelAsync(Contracts.Channel.HanbellModbusGateway, gatewayReadStatus, nowUtc, ct);
        await _alarmEngine.EvaluateChannelAsync(Contracts.Channel.Ddc1, ddc1.ReadStatus, nowUtc, ct);
        await _alarmEngine.EvaluateChannelAsync(Contracts.Channel.Ddc2, ddc2.ReadStatus, nowUtc, ct);

        // 2. 評估漢鐘冰水主機故障旗標、水溫門檻與保養時數告警
        if (_chillersByModbusId.TryGetValue(h1.ModbusId, out var c1))
            await _alarmEngine.EvaluateChillerAsync(c1.Id, h1, nowUtc, ct);
        if (_chillersByModbusId.TryGetValue(h2.ModbusId, out var c2))
            await _alarmEngine.EvaluateChillerAsync(c2.Id, h2, nowUtc, ct);

        // 3. 評估 DDC1 與 DDC2 各 FCU 設備之室內溫度告警門檻
        foreach (var fcu in ddc1.FcuList.Concat(ddc2.FcuList))
        {
            if (_fcuIdMap.TryGetValue((fcu.Channel, fcu.StationId, fcu.Position), out var mapped))
            {
                var ddcReadStatus = fcu.Channel == Contracts.Channel.Ddc1 ? ddc1.ReadStatus : ddc2.ReadStatus;
                await _alarmEngine.EvaluateFcuAsync(mapped.Id, mapped.Floor, fcu, ddcReadStatus, nowUtc, ct);
            }
        }
    }

    /// <summary>
    /// 依據節流策略評估是否將單台冰水主機之時序數據寫入資料庫。
    /// 【關鍵原則】：
    /// 1. 若該次讀取狀態不為 <see cref="Contracts.ReadStatus.Success"/>，快照值為 SDK 之預設 0 或舊快取，嚴禁寫入時序表以杜絕每日產生數萬筆髒資料。
    /// 2. 必須在節流判定前先檢查讀取狀態，確保設備自斷線恢復後的首筆有效讀值能正確通過節流判斷而立即落地。
    /// </summary>
    /// <param name="snapshot">冰水主機遙測快照物件。</param>
    /// <param name="nowUtc">目前作業之標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步寫入作業的取消語彙基元。</param>
    /// <returns>代表非同步作業的 <see cref="Task"/>。</returns>
    private async Task WriteChillerIfDueAsync(Contracts.ChillerSnapshot snapshot, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // 依 ModbusId 比對資料庫之實體設備
        if (!_chillersByModbusId.TryGetValue(snapshot.ModbusId, out var device))
        {
            _logger.LogWarning("找不到 ModbusId={ModbusId} 對應的 device_chiller，略過落地", snapshot.ModbusId);
            return;
        }

        // 讀取未成功時不落地：避免將斷線時 SDK 保留的零值寫入時序歷史
        if (snapshot.ReadStatus != Contracts.ReadStatus.Success) return;

        // 檢查是否已達冰機落地節流間隔（預設 10 秒）
        if (!_throttle.ShouldWriteChiller(snapshot.ModbusId, nowUtc)) return;

        // 執行資料庫時序批次寫入
        await _timeSeriesWriter.WriteChillerReadingsAsync(
            [(device.Id, nowUtc, snapshot)], ct);
    }

    /// <summary>
    /// 依據節流策略評估是否將整座 DDC 所轄的所有 FCU 時序數據批次寫入資料庫。
    /// 【關鍵原則】：
    /// 1. DDC 採整批輪詢機制，若 DDC 本身回報失敗，則整個清單中新舊數據雜揉且無法逐台標註有效性，整批略過不寫。
    /// 2. 逐台 FCU 檢驗節流條件：若開關/模式/風速變更，或溫度變化超過門檻（0.5°C），或超過未變更節流上限（60秒），即加入寫入批次。
    /// </summary>
    /// <param name="ddc">DDC 控制器遙測快照物件。</param>
    /// <param name="nowUtc">目前作業之標準 UTC 時間戳記。</param>
    /// <param name="ct">控制非同步寫入作業的取消語彙基元。</param>
    /// <returns>代表非同步批次寫入作業的 <see cref="Task"/>。</returns>
    private async Task WriteFcuListIfDueAsync(Contracts.DdcSnapshot ddc, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // 若 DDC 讀取非 Success 狀態，整批放棄寫入
        if (ddc.ReadStatus != Contracts.ReadStatus.Success) return;

        List<(int DeviceId, DateTimeOffset TsUtc, Contracts.FcuSnapshot Snapshot, Contracts.ReadStatus ReadStatus)>? rows = null;

        foreach (var fcu in ddc.FcuList)
        {
            var key = (fcu.Channel, fcu.StationId, fcu.Position);
            if (!_fcuIdMap.TryGetValue(key, out var mapped))
            {
                // 未註冊設備僅發布一次警告
                if (_warnedUnmappedFcu.TryAdd(key, true))
                {
                    _logger.LogWarning(
                        "找不到 Channel={Channel} StationId={StationId} Position={Position} 對應的 device_fcu，略過落地（只警告一次）",
                        fcu.Channel, fcu.StationId, fcu.Position);
                }
                continue;
            }

            // 檢查該 FCU 是否符合落地條件（數值異動或達到 60 秒上限）
            if (!_throttle.ShouldWriteFcu(fcu, nowUtc)) continue;

            (rows ??= []).Add((mapped.Id, ddc.UpdateTimeUtc, fcu, ddc.ReadStatus));
        }

        // 若有滿足條件的 FCU，一次性批次送入時序資料庫寫入器
        if (rows is { Count: > 0 })
        {
            await _timeSeriesWriter.WriteFcuReadingsAsync(rows, ct);
        }
    }

    // ------------------------------------------------------------------
    // 連線事件消費
    // ------------------------------------------------------------------

    /// <summary>
    /// 非同步連線事件消費者背景任務。
    /// 持續自 <see cref="_connectionChannel"/> 讀取通道狀態事件，更新看門狗區段紀錄、快取最新狀態並推播至 Web API。
    /// </summary>
    /// <param name="ct">控制消費者停止的取消語彙基元。</param>
    /// <returns>代表非同步消費作業的 <see cref="Task"/>。</returns>
    private async Task ConsumeConnectionEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _connectionChannel.Reader.ReadAllAsync(ct))
            {
                // 轉換為合約通道與連線狀態列舉
                var channel = e.ConnectionChannel.ToContractChannel();
                var state = e.ConnectionState.ToContract();
                var triggerTimeUtc = ToUtc(e.TriggerTime);

                // 更新看門狗內部連線狀態，判定是否開啟或關閉健康歷史區段
                await _watchdog.OnConnectionStatusChangedAsync(channel, state, triggerTimeUtc, ct);

                // 組裝狀態酬載物件
                var payload = new Contracts.ConnectionStatusPayload
                {
                    Channel = channel,
                    Ip = e.ConnectPath.ID,
                    Port = e.ConnectPath.Path,
                    ConnectionState = state,
                    IsConnected = e.IsConnected,
                    TriggerTimeUtc = triggerTimeUtc,
                };

                // 更新最新狀態字典，供監督迴圈定時重發
                _lastConnection[channel] = payload;

                // 即時推播連線狀態變更至 Web API
                await _ingestClient.PostConnectionStatusAsync(payload, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止作業流程，忽略取消例外
        }
    }

    // ------------------------------------------------------------------
    // 時間轉換：UpdateTime/TriggerTime 是 DateTime.Now（本機時間、無時區資訊），
    // 不依賴容器 TZ 環境變數，明確以 Asia/Taipei 解讀後轉 UTC，避免時區設定漂移。
    // ------------------------------------------------------------------

    /// <summary>
    /// 將供應商 DLL 回傳之無時區本機時間 (<see cref="DateTime"/>) 轉換為標準台北時區之 <see cref="DateTimeOffset"/> (UTC)。
    /// </summary>
    /// <param name="localNaive">供應商事件中攜帶之本機時間物件（Kind 為 Unspecified）。</param>
    /// <returns>轉換後帶有零偏移量 (+00:00) 的 UTC <see cref="DateTimeOffset"/>。</returns>
    private static DateTimeOffset ToUtc(DateTime localNaive)
    {
        var unspecified = DateTime.SpecifyKind(localNaive, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, TaipeiTimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    /// <summary>
    /// 安全解析台北時區物件。優先自作業系統查詢 "Asia/Taipei"，若於 Windows 精簡環境或找不到時區定義時，
    /// 安全降級建立固定的 UTC+8 自訂時區（台灣地區全年無日光節約時間，固定 8 小時偏移極為安全穩定）。
    /// </summary>
    /// <returns>可用的台北時區 <see cref="TimeZoneInfo"/> 實例。</returns>
    private static TimeZoneInfo ResolveTaipeiTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Asia/Taipei-Fixed", TimeSpan.FromHours(8), "Taipei (fixed)", "Taipei (fixed)");
        }
    }
}
