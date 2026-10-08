namespace Lookout
{
    internal static partial class WebUi
    {
        static void MapLogApi(WebApplication app)
        {
            app.MapGet("/api/log", (int? lines) =>
            {
                int n = lines ?? 200;
                string logDir = "/var/log/lookout/";
                if (!Directory.Exists(logDir))
                    return Results.Ok(new { lines = Array.Empty<string>() });

                var logFiles = Directory.GetFiles(logDir, "lookout_*.log")
                                        .OrderByDescending(f => f)
                                        .ToArray();

                if (logFiles.Length == 0)
                    return Results.Ok(new { lines = Array.Empty<string>() });

                var result = new List<string>();
                foreach (var file in logFiles)
                {
                    if (result.Count >= n) break;
                    var fileLines = File.ReadAllLines(file);
                    result.InsertRange(0, fileLines);
                }

                var tail = result.Skip(Math.Max(0, result.Count - n)).ToArray();
                return Results.Ok(new { lines = tail });
            });
        }
    }
}
