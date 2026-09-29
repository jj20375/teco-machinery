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

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TecoDbOptions>(builder.Configuration.GetSection(TecoDbOptions.SectionName));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

builder.Services.AddSingleton<TecoDbConnectionFactory>();
builder.Services.AddSingleton<DeviceRepository>();
builder.Services.AddSingleton<ChillerRepository>();
builder.Services.AddSingleton<FcuRepository>();
builder.Services.AddSingleton<AlarmRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<MerchantRepository>();
builder.Services.AddSingleton<MembershipRepository>();
builder.Services.AddSingleton<RoleRepository>();
builder.Services.AddSingleton<PermissionRepository>();
builder.Services.AddSingleton<OperationLogRepository>();
builder.Services.AddSingleton<FloorPlanRepository>();
builder.Services.AddSingleton<PartitionMaintenanceRepository>();
builder.Services.AddSingleton<RefreshTokenRepository>();
builder.Services.AddSingleton<PermissionGrantService>();
builder.Services.AddSingleton<CurrentStateStore>();
builder.Services.AddSingleton<ChannelHealthRepository>();
builder.Services.AddSingleton<DiagnosticsRepository>();
builder.Services.AddSingleton<ScheduledJobStatusStore>();
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddHttpClient(DiagnosticsService.CollectorHttpClientName, c => c.Timeout = TimeSpan.FromSeconds(2));
builder.Services.AddSingleton<JwtTokenService>();

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

builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new UtcDateTimeConverter());
    options.PayloadSerializerOptions.Converters.Add(new UtcNullableDateTimeConverter());
});
builder.Services.AddScoped<TelemetryBroadcaster>();
builder.Services.AddHostedService<RollupHostedService>();
builder.Services.AddHostedService<PartitionMaintenanceHostedService>();

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

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapIngestEndpoints();
app.MapRealtimeEndpoints();
app.MapChillerEndpoints();
app.MapFcuEndpoints();
app.MapAlarmEndpoints();
app.MapAuthEndpoints();
app.MapPlatformEndpoints();
app.MapDiagnosticsEndpoints();
app.MapMerchantEndpoints();
app.MapFloorPlanEndpoints();
app.MapThresholdEndpoints();
app.MapPublicEndpoints();
app.MapHub<TelemetryHub>("/hubs/telemetry");

app.Run();
