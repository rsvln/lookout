namespace Lookout
{
    internal static partial class WebUi
    {
        static void MapConfigApi(WebApplication app, string configPath)
        {
            app.MapGet("/api/config", () =>
            {
                if (!File.Exists(configPath))
                    return Results.NotFound();
                return Results.Ok(new { content = File.ReadAllText(configPath) });
            });

            // Saves the config (after checking that it parses); with "apply": true also applies it.
            app.MapPost("/api/config", async (HttpRequest req) =>
            {
                using var reader = new StreamReader(req.Body);
                var data = System.Text.Json.JsonSerializer.Deserialize<ConfigPayload>(await reader.ReadToEndAsync());
                if (data?.content == null)
                    return Results.BadRequest();
                try
                {
                    ParseSettings(data.content);
                }
                catch (YamlDotNet.Core.YamlException ex)
                {
                    // The innermost message names the actual problem; the outer ones only wrap it.
                    var inner = ex;
                    while (inner.InnerException is YamlDotNet.Core.YamlException next)
                        inner = next;
                    string message = inner.InnerException?.Message ?? inner.Message;
                    if (message.StartsWith("No node deserializer"))
                        message = "unexpected value (wrong type or indentation)";
                    return Results.BadRequest(new { ok = false, error = $"line {inner.Start.Line}, column {inner.Start.Column}: {message}" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { ok = false, error = ex.Message });
                }

                if (File.Exists(configPath))
                    File.Copy(configPath, configPath + ".bak", overwrite: true);
                File.WriteAllText(configPath, data.content);
                Program.Log("app", "", "", "Settings saved");
                return data.apply ? await ApplyAsync(configPath) : Results.Ok(new { ok = true });
            });

            // Re-reads the saved config file and restarts the services with it.
            app.MapPost("/api/apply", () => ApplyAsync(configPath));
        }

        static void MapStatusApi(WebApplication app)
        {
            // Polled by the page after applying settings, until MQTT is connected again.
            app.MapGet("/api/status", () => Results.Ok(new { version = VersionInfo.Version, mqtt = Program.mqttClient?.IsConnected == true, locale = L10n.Web.Locale }));
        }
    }
}
