using Teco.Hvac.Api.Auth;

namespace Teco.Hvac.Api.Services;

/// <summary>
/// 補記「權限不足被拒（403）」的寫入類請求。Handler 裡大量的 Results.Forbid() 不會各自寫紀錄，
/// 統一在這裡攔：已登入、方法是 POST/PUT/PATCH/DELETE、回應 403、而且這次請求沒有任何人寫過紀錄
/// （handler 自己記過更詳細說明的拒絕會在 OperationLogger 做記號，這裡就不重複）。
/// GET 的 403 不記（讀取被拒太吵）；未登入的 401 也不記（沒有操作者身分，而且會被掃描流量灌爆）。
/// 寫紀錄失敗只吞掉並記 log，絕不能因此讓原本的回應壞掉。
/// </summary>
public static class DeniedRequestAudit
{
    public static IApplicationBuilder UseDeniedRequestAudit(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        await next();

        if (ctx.Response.StatusCode != StatusCodes.Status403Forbidden) return;
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method)) return;
        if (!ctx.Request.Path.StartsWithSegments("/api")) return;
        if (ctx.Items.ContainsKey(OperationLogger.LoggedMarker)) return;
        if (!RequestScope.TryRead(ctx.User, out var scope) || scope is null) return;

        try
        {
            var pattern = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? ctx.Request.Path.Value ?? "";
            var target = $"{ctx.Request.Method} {pattern}";
            await ctx.RequestServices.GetRequiredService<OperationLogger>().LogDeniedAsync(
                scope, OperationActionLabels.Denied, "endpoint", target.Length <= 64 ? target : target[..64],
                $"沒有權限，操作被拒絕：{ctx.Request.Method} {ctx.Request.Path.Value}", "權限不足", ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILogger<OperationLogger>>().LogWarning(ex, "補記權限拒絕的操作紀錄失敗");
        }
    });
}
