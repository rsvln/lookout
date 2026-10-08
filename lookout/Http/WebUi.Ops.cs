namespace Lookout
{
    internal static partial class WebUi
    {
        // Paths served without the web login: they carry no event data, and Docker's HEALTHCHECK has no credentials.
        static readonly string[] openPaths = { "/health" };

        // Operations: liveness for Docker / load balancers, metrics for Prometheus.
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
