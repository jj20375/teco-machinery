using Teco.Hvac.Collector;
using Teco.Hvac.Infrastructure;
using Teco.Hvac.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection(CollectorOptions.SectionName));
builder.Services.Configure<TecoDbOptions>(builder.Configuration.GetSection(TecoDbOptions.SectionName));

builder.Services.AddSingleton<TecoDbConnectionFactory>();
builder.Services.AddSingleton<DeviceRepository>();
builder.Services.AddSingleton<TimeSeriesWriter>();
builder.Services.AddSingleton<ChannelHealthRepository>();
builder.Services.AddSingleton<AlarmRepository>();
builder.Services.AddSingleton<AlarmEngine>();
builder.Services.AddSingleton<ChannelWatchdog>();
builder.Services.AddSingleton<ThrottlePolicy>();

builder.Services.AddHttpClient<IngestHttpClient>((sp, client) =>
{
    var apiBaseUrl = builder.Configuration[$"{CollectorOptions.SectionName}:ApiBaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiBaseUrl))
    {
        client.BaseAddress = new Uri(apiBaseUrl);
    }
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddSingleton<CollectorHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorHostedService>());

var app = builder.Build();

// 給 Docker healthcheck 用：DEGRADED（任一通道逾時未成功讀取）仍回 200，
// 因為服務本身沒死、只是設備暫時連不上；真正掛掉是完全沒回應（Docker 對 timeout 判斷）。
app.MapGet("/healthz", (CollectorHostedService collector) =>
{
    var now = DateTimeOffset.UtcNow;
    bool degraded = collector.Watchdog.IsDegraded(now);
    return Results.Ok(new { status = degraded ? "degraded" : "healthy", timestampUtc = now });
});

app.Run();
