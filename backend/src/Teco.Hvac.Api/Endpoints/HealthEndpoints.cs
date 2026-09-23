using Dapper;
using Teco.Hvac.Infrastructure;

namespace Teco.Hvac.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

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
