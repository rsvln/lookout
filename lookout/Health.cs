using System.Diagnostics;

namespace Lookout
{
    public class HealthCheck
    {
        public bool ok { get; set; }
        public string detail { get; set; }
    }

    public class HealthReport
    {
        public string status { get; set; }
        public string version { get; set; }
        public Dictionary<string, HealthCheck> checks { get; set; } = new Dictionary<string, HealthCheck>();
        public bool Healthy => checks.Values.All(c => c.ok);
    }

    // GET /health: can the app do its job? MQTT connected, Frigate's API answering, ffmpeg runnable, Frigate's database there.
    // The slow checks (HTTP, ffmpeg) are remembered for a few seconds so frequent polling does not load Frigate.
    public static class Health
    {
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);
        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        static DateTime slowCheckedAt = DateTime.MinValue;
        static HealthCheck frigateCache, ffmpegCache;

        public static async Task<HealthReport> CheckAsync()
        {
            var report = new HealthReport { version = VersionInfo.Version };
            report.checks["mqtt"] = Program.mqttClient?.IsConnected == true
                ? new HealthCheck { ok = true }
                : new HealthCheck { ok = false, detail = "not connected to " + Program.settings?.mqtt?.host + ":" + Program.settings?.mqtt?.port };
            report.checks["db"] = CheckDb();

            await gate.WaitAsync();
            try
            {
                if (DateTime.UtcNow - slowCheckedAt > CacheFor)
                {
                    frigateCache = await CheckFrigateAsync();
                    ffmpegCache = await CheckFfmpegAsync();
                    slowCheckedAt = DateTime.UtcNow;
                }
                report.checks["frigate"] = frigateCache;
                report.checks["ffmpeg"] = ffmpegCache;
            }
            finally
            {
                gate.Release();
            }

            report.status = report.Healthy ? "ok" : "unhealthy";
            return report;
        }

        // Forgets the remembered slow checks (tests).
        public static void ResetCache() => slowCheckedAt = DateTime.MinValue;

        static HealthCheck CheckDb()
        {
            string path = Program.settings?.frigate?.dbpath;
            if (string.IsNullOrEmpty(path))
                return new HealthCheck { ok = false, detail = "frigate.dbpath is not set" };
            return System.IO.File.Exists(path)
                ? new HealthCheck { ok = true }
                : new HealthCheck { ok = false, detail = path + " not found" };
        }

        static async Task<HealthCheck> CheckFrigateAsync()
        {
            var f = Program.settings?.frigate;
            if (f == null || string.IsNullOrEmpty(f.host))
                return new HealthCheck { ok = false, detail = "frigate.host is not set" };
            try
            {
                using var response = await http.GetAsync("http://" + f.host + ":" + f.port + "/api/version");
                return response.IsSuccessStatusCode
                    ? new HealthCheck { ok = true }
                    : new HealthCheck { ok = false, detail = "HTTP " + (int)response.StatusCode };
            }
            catch (Exception ex)
            {
                return new HealthCheck { ok = false, detail = ex.Message };
            }
        }

        static async Task<HealthCheck> CheckFfmpegAsync()
        {
            try
            {
                var psi = new ProcessStartInfo("ffmpeg", "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _ = p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync(cts.Token);
                return p.ExitCode == 0 ? new HealthCheck { ok = true } : new HealthCheck { ok = false, detail = "exit code " + p.ExitCode };
            }
            catch (Exception ex)
            {
                return new HealthCheck { ok = false, detail = ex.Message };
            }
        }
    }
}
