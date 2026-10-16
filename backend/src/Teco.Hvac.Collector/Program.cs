using Teco.Hvac.Collector;
using Teco.Hvac.Infrastructure;
using Teco.Hvac.Infrastructure.Repositories;

// ============================================================================
// 東元智慧空調資料採集服務 (Teco.Hvac.Collector) 啟動程式進入點
// 職責：配置 DI 容器、載入組態、建立 HTTP 攝取用戶端、註冊核心託管服務並提供 /healthz 容器健康度檢查端點
// ============================================================================

// 建立 Web 應用程式建構器實例
var builder = WebApplication.CreateBuilder(args);

// 1. 綁定組態選項
// 綁定採集服務核心參數（包含漢鐘/DDC IP 與連接埠、節流週期、看門狗門檻等）
builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection(CollectorOptions.SectionName));
// 綁定 MariaDB 資料庫連線字串與集區選項
builder.Services.Configure<TecoDbOptions>(builder.Configuration.GetSection(TecoDbOptions.SectionName));

// 2. 註冊基礎設施與資料存取倉儲（皆採用 Singleton 生命週期，供背景常駐服務重複使用）
// 資料庫連線工廠
builder.Services.AddSingleton<TecoDbConnectionFactory>();
// 設備主檔資料倉儲（查詢主機與 FCU 對照資訊）
builder.Services.AddSingleton<DeviceRepository>();
// 時序資料高吞吐批次寫入器
builder.Services.AddSingleton<TimeSeriesWriter>();
// 通道健康狀態歷史紀錄倉儲
builder.Services.AddSingleton<ChannelHealthRepository>();
// 告警事件與規則資料倉儲
builder.Services.AddSingleton<AlarmRepository>();
// 冰水主機保養時數追蹤倉儲
builder.Services.AddSingleton<ChillerMaintenanceRepository>();
// 告警計算評估引擎
builder.Services.AddSingleton<AlarmEngine>();
// 通道通訊看門狗
builder.Services.AddSingleton<ChannelWatchdog>();
// 時序落地寫入節流策略
builder.Services.AddSingleton<ThrottlePolicy>();

// 3. 註冊推送至 Web API 的具型 HTTP 客戶端 (IngestHttpClient)
builder.Services.AddHttpClient<IngestHttpClient>((sp, client) =>
{
    var apiBaseUrl = builder.Configuration[$"{CollectorOptions.SectionName}:ApiBaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiBaseUrl))
    {
        client.BaseAddress = new Uri(apiBaseUrl);
    }
    // 內部推播請求設定 5 秒超時，防止網路異常時請求堆疊
    client.Timeout = TimeSpan.FromSeconds(5);
});

// 4. 註冊採集器核心託管服務 (CollectorHostedService)
builder.Services.AddSingleton<CollectorHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorHostedService>());

// 建置 Web 應用程式實例
var app = builder.Build();

// 5. 註冊容器健康檢查端點 (/healthz)
// 供 Docker Compose 與容器編排工具確認服務健康狀況：
// 若任一通道逾時未成功讀取，回傳 status="degraded"，但 HTTP 狀態碼仍為 200，
// 因為採集服務本身仍處於存活狀態，僅硬體暫時通訊中斷；唯有服務徹底無回應時才會被判定為失敗。
app.MapGet("/healthz", (CollectorHostedService collector) =>
{
    var now = DateTimeOffset.UtcNow;
    bool degraded = collector.Watchdog.IsDegraded(now);
    return Results.Ok(new { status = degraded ? "degraded" : "healthy", timestampUtc = now });
});

// 啟動主機並監聽端點
app.Run();
