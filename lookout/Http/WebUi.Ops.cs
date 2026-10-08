namespace Lookout
{
    internal static partial class WebUi
    {
        // Operations: liveness for Docker / load balancers (/health is open: it has no event data and the
        // HEALTHCHECK has no credentials), metrics for Prometheus (behind the login, Basic works for scrapers).
        static void MapOpsApi(WebApplication app)
        {
            app.MapGet("/health", async () =>
            {
                var report = await Health.CheckAsync();
                return Results.Json(report, statusCode: report.Healthy ? 200 : 503);
            });

            app.MapGet("/metrics", () => Results.Text(Metrics.Render(), "text/plain; version=0.0.4; charset=utf-8"));
        }
    }
}
