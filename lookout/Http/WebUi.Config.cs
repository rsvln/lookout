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
                return Results.Ok(new { content = ConfigYaml.MaskSecrets(File.ReadAllText(configPath)) });
            });

            // Structured form next to the YAML editor; secrets are the mask unless the field was changed.
            app.MapGet("/api/settings", () => Safe(() =>
            {
                if (!File.Exists(configPath))
                    return Results.NotFound();
                return Results.Ok(SettingsForm.Snapshot(ParseSettings(File.ReadAllText(configPath))));
            }));

            app.MapPost("/api/settings", async (HttpRequest req) =>
            {
                using var reader = new StreamReader(req.Body);
                var data = System.Text.Json.JsonSerializer.Deserialize<SettingsPayload>(await reader.ReadToEndAsync());
                if (data?.fields == null)
                    return Results.BadRequest();
                if (!File.Exists(configPath))
                    return Results.NotFound();
                string yaml = File.ReadAllText(configPath);
                if (data.removeCameras != null)
                    foreach (var name in data.removeCameras.Where(n => n != null && ConfigYaml.CameraNameOk.IsMatch(n)))
                        yaml = ConfigYaml.Remove(yaml, "frigate.cameras[camera=" + name + "]");
                yaml = ConfigYaml.Apply(yaml, data.fields);
                return await SaveYaml(configPath, yaml, data.apply);
            });

            // Saves the config (after checking that it parses); with "apply": true also applies it.
            // Secrets still shown as the mask are taken from the file so a save cannot wipe them.
            app.MapPost("/api/config", async (HttpRequest req) =>
            {
                using var reader = new StreamReader(req.Body);
                var data = System.Text.Json.JsonSerializer.Deserialize<ConfigPayload>(await reader.ReadToEndAsync());
                if (data?.content == null)
                    return Results.BadRequest();
                string content = File.Exists(configPath) ? ConfigYaml.RestoreSecrets(data.content, File.ReadAllText(configPath)) : data.content;
                return await SaveYaml(configPath, content, data.apply);
            });

            app.MapPost("/api/apply", () => ApplyAsync(configPath));
        }

        record SettingsPayload(Dictionary<string, string> fields, bool apply, string[] removeCameras = null);

        static async Task<IResult> SaveYaml(string configPath, string content, bool apply)
        {
            try
            {
                ParseSettings(content);
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
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
            File.WriteAllText(configPath, content);
            Program.Log("app", "", "", "Settings saved");
            return apply ? await ApplyAsync(configPath) : Results.Ok(new { ok = true });
        }

        static void MapStatusApi(WebApplication app)
        {
            // Polled by the page after applying settings, until MQTT is connected again.
            app.MapGet("/api/status", () => Results.Ok(new { version = VersionInfo.Version, mqtt = Program.mqttClient?.IsConnected == true, locale = L10n.Web.Locale }));
        }
    }
}
