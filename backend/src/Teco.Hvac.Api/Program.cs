using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Teco.Hvac.Api.Auth;
using Teco.Hvac.Api.Endpoints;
using Teco.Hvac.Api.Hubs;
using Teco.Hvac.Api.Services;
using Teco.Hvac.Api.Services.Diagnostics;
using Teco.Hvac.Infrastructure;
using Teco.Hvac.Infrastructure.Permissions;
using Teco.Hvac.Infrastructure.Repositories;

// 建立 Web 應用程式建構器實例
var builder = WebApplication.CreateBuilder(args);

// 綁定資料庫連線選項與 JWT 驗證組態
builder.Services.Configure<TecoDbOptions>(builder.Configuration.GetSection(TecoDbOptions.SectionName));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

// 註冊資料庫連線處理工廠（單例生命週期）
builder.Services.AddSingleton<TecoDbConnectionFactory>();

// 註冊基礎設施領域倉儲（單例生命週期）
builder.Services.AddSingleton<DeviceRepository>();
builder.Services.AddSingleton<ChillerRepository>();
builder.Services.AddSingleton<FcuRepository>();
builder.Services.AddSingleton<AlarmRepository>();
builder.Services.AddSingleton<ChillerMaintenanceRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<MerchantRepository>();
builder.Services.AddSingleton<MembershipRepository>();
builder.Services.AddSingleton<RoleRepository>();
builder.Services.AddSingleton<PermissionRepository>();
builder.Services.AddSingleton<OperationLogRepository>();
builder.Services.AddSingleton<FloorPlanRepository>();
builder.Services.AddSingleton<PartitionMaintenanceRepository>();
builder.Services.AddSingleton<RefreshTokenRepository>();

// 註冊角色權限計算服務與記憶體狀態快取
builder.Services.AddSingleton<PermissionGrantService>();
builder.Services.AddSingleton<CurrentStateStore>();
builder.Services.AddSingleton<ChannelHealthRepository>();
builder.Services.AddSingleton<DiagnosticsRepository>();
builder.Services.AddSingleton<ScheduledJobStatusStore>();
builder.Services.AddSingleton<DiagnosticsService>();

// 註冊用於向 Collector 探詢狀態的 HttpClient
builder.Services.AddHttpClient(DiagnosticsService.CollectorHttpClientName, c => c.Timeout = TimeSpan.FromSeconds(2));

// 註冊 JWT 簽發服務
builder.Services.AddSingleton<JwtTokenService>();

// 註冊 HTTP 請求上下文存取器與作用域操作日誌記錄器
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<OperationLogger>();

// 所有時序資料在 DB 都是 UTC，但 Dapper 讀出來的 DateTime.Kind 是 Unspecified，S.T.J 預設
// 序列化時不會加 'Z'，前端在 Asia/Taipei（UTC+8）瀏覽器會把它當成本地時間解讀，導致畫面上
// 所有時間都少 8 小時且沒有任何錯誤——見 UtcDateTimeConverter 上的完整說明。HTTP JSON 跟
// SignalR 的 JSON Hub Protocol 是兩份獨立的序列化設定，兩邊都要掛，漏一邊還是會有問題。
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new UtcDateTimeConverter());
    options.SerializerOptions.Converters.Add(new UtcNullableDateTimeConverter());
});

// 設定 SignalR 即時推播服務與其 JSON 序列化器
builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new UtcDateTimeConverter());
    options.PayloadSerializerOptions.Converters.Add(new UtcNullableDateTimeConverter());
});
// 註冊即時遙測廣播服務
builder.Services.AddScoped<TelemetryBroadcaster>();

// 註冊背景託管排程服務：每分鐘時序資料聚合服務與資料表分區維護服務
builder.Services.AddHostedService<RollupHostedService>();
builder.Services.AddHostedService<PartitionMaintenanceHostedService>();

// 設定 JWT Bearer 身分驗證機制
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 關鍵：關掉預設的 inbound claim 改寫，否則 ASP.NET Core 會把 "sub" 這類短名稱
        // 換成長版 XML schema URI，RequestScope.TryRead 用短名稱找 claim 會全部落空。
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["SigningKey"]!)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "displayName",
        };

        options.Events = new JwtBearerEvents
        {
            // SignalR 透過 querystring 帶 access_token（瀏覽器 WebSocket 無法自訂 Header）。
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },

            // 帳號停用、密碼重設、角色/權限或場館 CRUD-子項開關變更時 AuthVersion 會遞增，
            // 這裡即時比對資料庫，讓已簽發的舊 JWT 立刻失效，不用等到過期。
            OnTokenValidated = async context =>
            {
                if (!int.TryParse(context.Principal?.FindFirstValue("sub"), out var userId))
                {
                    context.Fail("登入主體無效。");
                    return;
                }

                var users = context.HttpContext.RequestServices.GetRequiredService<UserRepository>();
                var user = await users.FindByIdAsync(userId, context.HttpContext.RequestAborted);
                if (user is null || !user.IsActive)
                {
                    context.Fail("帳號已停用。");
                    return;
                }

                if (!int.TryParse(context.Principal!.FindFirstValue("auth_version"), out var tokenVersion) || tokenVersion != user.AuthVersion)
                {
                    context.Fail("登入權限已更新，請重新登入。");
                    return;
                }

                var merchantIdClaim = context.Principal.FindFirstValue("merchant_id");
                if (int.TryParse(merchantIdClaim, out var merchantId))
                {
                    var merchants = context.HttpContext.RequestServices.GetRequiredService<MerchantRepository>();
                    var merchant = await merchants.FindByIdAsync(merchantId, context.HttpContext.RequestAborted);
                    if (merchant is null || merchant.Status != "active")
                    {
                        context.Fail("所屬場館已停用。");
                    }
                }
            },
        };
    });
builder.Services.AddAuthorization();

// 設定跨來源資源共享 (CORS) 政策
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }
    });
});

// 建置 ASP.NET Core WebApplication 實例
var app = builder.Build();

// 設定 HTTP 請求處理管線中介軟體 (Middleware Pipeline)
app.UseCors();
app.UseAuthentication();
// 放在 UseAuthorization 之前包住它：授權層回的 403 也要看得到
app.UseDeniedRequestAudit();
app.UseAuthorization();

// 註冊各功能模組路由端點 (Minimal API Endpoints)
app.MapHealthEndpoints();
app.MapIngestEndpoints();
app.MapRealtimeEndpoints();
app.MapChillerEndpoints();
app.MapChillerMaintenanceEndpoints();
app.MapFcuEndpoints();
app.MapAlarmEndpoints();
app.MapAuthEndpoints();
app.MapPlatformEndpoints();
app.MapDiagnosticsEndpoints();
app.MapMerchantEndpoints();
app.MapFloorPlanEndpoints();
app.MapThresholdEndpoints();
app.MapPublicEndpoints();

// 註冊 SignalR 即時推播中心路由
app.MapHub<TelemetryHub>("/hubs/telemetry");

// 啟動 Web 服務監聽
app.Run();

