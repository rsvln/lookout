namespace Lookout
{
    internal static partial class WebUi
    {
        // The page and its static files. The script and the page are localized on every request (the language can change on apply).
        static void MapPages(WebApplication app)
        {
            // The page itself, at every address it handles (the script picks the view from the path).
            foreach (var path in new[] { "/", "/log", "/last", "/stats", "/stats/events", "/config", "/about", "/event/{id}" })
                app.MapGet(path, () => Results.Content(Localize(Asset("index.html")), "text/html; charset=utf-8"));

            app.MapGet("/js/app.js", () => Results.Content(Localize(Asset("app.js")), "text/javascript; charset=utf-8"));
            app.MapGet("/css/app.css", () => Results.Content(Asset("app.css"), "text/css; charset=utf-8"));

            // YAML editor for the Config tab, bundled from webui/ (npm run build), served locally so no internet is needed.
            app.MapGet("/js/yaml-editor.js", () =>
            {
                string path = Path.Combine(Program.appLocation, "web", "yaml-editor.js");
                return File.Exists(path) ? Results.File(path, "text/javascript") : Results.NotFound();
            });

            // Frigate's logo in red.
            app.MapGet("/favicon.svg", () =>
            {
                string path = Path.Combine(Program.appLocation, "web", "favicon.svg");
                return File.Exists(path) ? Results.File(path, "image/svg+xml") : Results.NotFound();
            });

            // Version and the README rendered to HTML for the About tab.
            app.MapGet("/api/about", () => Safe(() =>
            {
                string readmePath = Path.Combine(Program.appLocation, "README.md");
                string readme = File.Exists(readmePath)
                    ? Markdig.Markdown.ToHtml(File.ReadAllText(readmePath), Markdig.MarkdownExtensions.UseAdvancedExtensions(new Markdig.MarkdownPipelineBuilder()).Build())
                    : "";
                return Results.Ok(new { version = VersionInfo.Version, build = VersionInfo.BuildDate, url = VersionInfo.ProjectUrl, readme });
            }));
        }
    }
}
