using Dapper;
using Teco.Hvac.Infrastructure;

namespace Teco.Hvac.Api.Endpoints;

/// <summary>
/// 服務存活與健康檢查 API 端點群組。
/// 提供容器編排（Docker Compose / K8s）探測所需的 liveness (/healthz) 與 readiness (/readyz) 端點。
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// 註冊健康與就緒檢查相關 HTTP 端點路由。
    /// </summary>
    /// <param name="app">WebApplication 實體。</param>
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // 存活探測 (Liveness Probe)：只要處理程序活著就回傳 200 OK
        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

        // 就緒探測 (Readiness Probe)：驗證資料庫連線是否可正常執行查詢
        app.MapGet("/readyz", async (TecoDbConnectionFactory dbFactory, CancellationToken ct) =>
        {
            try
            {
                using var conn = await dbFactory.CreateOpenAsync(ct);
                await conn.ExecuteScalarAsync<int>("SELECT 1");
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
