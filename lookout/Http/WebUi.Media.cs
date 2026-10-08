namespace Lookout
{
    internal static partial class WebUi
    {
        static void MapMediaApi(WebApplication app)
        {
            // The id is looked up in Frigate's DB, so only real snapshot files can be served.
            app.MapGet("/api/snapshot/{id}", async (string id) =>
            {
                try
                {
                    var ev = StatsService.ResolveEvent(id);
                    var bytes = ev == null ? null : await StatsService.GetSnapshotAsync(ev);
                    return bytes == null ? Results.NotFound() : Results.File(bytes, "image/jpeg");
                }
                catch (Exception ex) { return Results.Problem(ex.Message); }
            });

            // Event clip built from Frigate's recording segments (up to now for events in progress); ?download=1 sends it as an attachment.
            app.MapGet("/api/clip/{id}", async (string id, int? download) =>
            {
                try
                {
                    var ev = StatsService.ResolveEvent(id);
                    string path = ev == null ? null : await StatsService.GetClipPathAsync(ev);
                    if (path == null)
                        return Results.NotFound();
                    return download == 1
                        ? Results.File(path, "video/mp4", ev.camera + "-" + ev.id + ".mp4", enableRangeProcessing: true)
                        : Results.File(path, "video/mp4", enableRangeProcessing: true);
                }
                catch (Exception ex) { return Results.Problem(ex.Message); }
            });
        }
    }
}
