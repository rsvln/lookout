namespace Lookout
{
    internal static partial class WebUi
    {
        // Events and statistics, all read from Frigate's DB through StatsService.
        static void MapEventApi(WebApplication app)
        {
            app.MapGet("/api/meta", () => Safe(() => Results.Ok(StatsService.GetMeta())));

            app.MapGet("/api/last", (string camera, string label, int? limit) => Safe(() =>
            {
                camera = string.IsNullOrEmpty(camera) ? null : camera;
                label = string.IsNullOrEmpty(label) ? null : label;
                // Without a camera: last `limit` events of every camera; with a camera: last `limit` of that camera.
                var rows = camera == null
                    ? StatsService.GetLastPerCamera(Math.Clamp(limit ?? 1, 1, 50), label)
                    : StatsService.GetLast(camera, label, Math.Clamp(limit ?? 24, 1, 200));
                return Results.Ok(rows.Select(EventJson));
            }));

            app.MapGet("/api/event/{id}", (string id) => Safe(() =>
            {
                var ev = StatsService.GetEvent(id);
                return ev == null ? Results.NotFound() : Results.Ok(EventJson(ev));
            }));

            // Events behind a Stats view (same period / camera / label rules as /api/stat).
            app.MapGet("/api/events", (string period, string camera, string label, int? hour, string day, int? limit) => Safe(() =>
            {
                bool configOnly = label == "config";
                var rows = StatsService.GetPeriodEvents(period, string.IsNullOrEmpty(camera) ? null : camera,
                                                        configOnly || string.IsNullOrEmpty(label) ? null : label,
                                                        configOnly, hour, string.IsNullOrEmpty(day) ? null : day,
                                                        Math.Clamp(limit ?? 200, 1, 500), out int total);
                return Results.Ok(new
                {
                    total,
                    events = rows.Select(EventJson)
                });
            }));

            // label: empty = all objects, "config" = what the bot is configured to send, otherwise a single label.
            // format=csv|json (json is the default); csv kind=matrix|hours|heatmap.
            app.MapGet("/api/stat", (string period, string camera, string label, string format, string kind) => Safe(() =>
            {
                bool configOnly = label == "config";
                var st = StatsService.GetStats(period,
                                               string.IsNullOrEmpty(camera) ? null : camera,
                                               string.IsNullOrEmpty(label) || configOnly ? null : label,
                                               configOnly);
                if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                    return Results.File(System.Text.Encoding.UTF8.GetBytes(StatsService.ToCsv(st, kind)),
                                        "text/csv; charset=utf-8", "lookout-stats.csv");
                return Results.Ok(st);
            }));

            app.MapGet("/api/search", (string q, string camera, string label, string from, string to, int? limit) => Safe(() =>
            {
                var rows = StatsService.Search(q, EmptyToNull(camera), EmptyToNull(label), ParseWhen(from), ParseWhen(to),
                                               Math.Clamp(limit ?? 100, 1, 500), out int total);
                return Results.Ok(new { total, events = rows.Select(EventJson) });
            }));
        }

        static string EmptyToNull(string s) => string.IsNullOrEmpty(s) ? null : s;

        // datetime-local ("2026-10-08T14:30") is wall time in the Lookout locale (options.timeoffset).
        static double? ParseWhen(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double unix))
                return unix;
            string[] formats = { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };
            if (!DateTime.TryParseExact(s.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.None, out var dt))
                return null;
            return (dt - DateTime.UnixEpoch).TotalSeconds - Program.settings.options.timeoffset * 60;
        }
    }
}
