using System.Collections.Concurrent;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lookout
{
    // The web UI: Kestrel host, auth middleware and shared helpers. Endpoints live in WebUi.*.cs, grouped by area;
    // the page itself (HTML / CSS / JS) is in web/ next to the binary.
    internal static partial class WebUi
    {
        public static void Start(string configPath)
        {
            Task.Run(() =>
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls("http://+:8888");
                builder.Logging.ClearProviders();
                var app = builder.Build();

                // Optional HTTP Basic auth for the whole UI and API (web.user / web.password in the config).
                app.Use(async (context, next) =>
                {
                    var web = Program.settings?.web;
                    if (web == null || string.IsNullOrEmpty(web.user) || string.IsNullOrEmpty(web.password) || IsAuthorized(context.Request, web))
                    {
                        await next();
                        return;
                    }
                    context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Lookout\", charset=\"UTF-8\"";
                    context.Response.StatusCode = 401;
                });

                MapPages(app);
                MapLogApi(app);
                MapEventApi(app);
                MapMediaApi(app);
                MapConfigApi(app, configPath);
                MapStatusApi(app);

                app.Run();
            });
        }

        record ConfigPayload(string content, bool apply);

        static bool IsAuthorized(HttpRequest request, WebSettings web)
        {
            string header = request.Headers.Authorization.ToString();
            if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return false;
            string decoded;
            try { decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim())); }
            catch (FormatException) { return false; }
            int colon = decoded.IndexOf(':');
            if (colon < 0)
                return false;
            // Constant-time comparison, so the password can't be guessed from response timing.
            static bool Same(string a, string b) => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
            return Same(decoded.Substring(0, colon), web.user) & Same(decoded.Substring(colon + 1), web.password);
        }

        static readonly SemaphoreSlim applyLock = new SemaphoreSlim(1, 1);

        // Parses the YAML and checks the sections the app cannot run without.
        internal static SettingsFile ParseSettings(string yaml)
        {
            var s = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build()
                .Deserialize<SettingsFile>(yaml);
            var missing = new[] { ("frigate", (object)s?.frigate), ("mqtt", s?.mqtt), ("telegram", s?.telegram), ("options", s?.options), ("logger", s?.logger) }
                .Where(x => x.Item2 == null).Select(x => x.Item1).ToList();
            if (missing.Count > 0)
                throw new InvalidDataException("missing section: " + string.Join(", ", missing));
            return s;
        }

        // Loads the config file and restarts Telegram polling, MQTT and the AI/FR queues (Program.Initialize).
        static async Task<IResult> ApplyAsync(string configPath)
        {
            await applyLock.WaitAsync();
            try
            {
                Program.settings = ParseSettings(File.ReadAllText(configPath));
                await Program.Initialize();
                Program.Log("app", "", "", "Settings applied");
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                Program.Log("app", "", "", "Failed to apply settings: " + ex.Message);
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
            finally
            {
                applyLock.Release();
            }
        }

        // Static page files (web/index.html, app.js, app.css), read once.
        static readonly ConcurrentDictionary<string, string> assetCache = new();
        static string Asset(string name) => assetCache.GetOrAdd(name, n => File.ReadAllText(Path.Combine(Program.appLocation, "web", n)));

        // Fills {{key}} placeholders in the page and hands web./label. strings to its scripts as I18N.
        internal static string Localize(string html)
        {
            html = System.Text.RegularExpressions.Regex.Replace(html, @"\{\{([\w.]+)\}\}", m => System.Net.WebUtility.HtmlEncode(L10n.Web.T(m.Groups[1].Value)));
            html = html.Replace("%VERSION%", System.Net.WebUtility.HtmlEncode(VersionInfo.Version))
                       .Replace("%BUILD%", System.Net.WebUtility.HtmlEncode(VersionInfo.BuildDate))
                       .Replace("%URL%", VersionInfo.ProjectUrl)
                       .Replace("%WEBLOCALE%", System.Net.WebUtility.HtmlEncode(L10n.Web.Locale));
            return html.Replace("/*I18N*/{}", System.Text.Json.JsonSerializer.Serialize(L10n.Web.Export("web.", "label.")));
        }

        static object EventJson(EventRow r) => new
        {
            r.id, r.camera, r.label, r.sub_label, r.score, r.start_time, r.end_time, r.zones, r.has_snapshot, r.has_clip,
            start_local = StatsService.ToLocal(r.start_time).ToString("yyyy-MM-dd HH:mm:ss")
        };

        static IResult Safe(Func<IResult> f)
        {
            try { return f(); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        }
    }
}
