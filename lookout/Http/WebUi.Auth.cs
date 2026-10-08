namespace Lookout
{
    internal static partial class WebUi
    {
        // Applies WebAuth.Decide to every request; the role of the caller goes to HttpContext.Items["role"].
        static async Task Authorize(HttpContext context, Func<Task> next)
        {
            var web = Program.settings?.web;
            var req = context.Request;
            var decision = WebAuth.Decide(web, new AuthRequest
            {
                Method = req.Method,
                Path = req.Path.Value ?? "/",
                WantsHtml = req.Headers.Accept.ToString().Contains("text/html"),
                AuthorizationHeader = req.Headers.Authorization.ToString(),
                SessionCookie = req.Cookies[WebAuth.CookieName],
                Token = req.Query["token"].FirstOrDefault()
            }, DateTime.UtcNow);

            context.Items["role"] = decision.Role;
            context.Items["user"] = decision.User;
            switch (decision.Outcome)
            {
                case AuthOutcome.Allow:
                    await next();
                    break;
                case AuthOutcome.Challenge:
                    context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Lookout\", charset=\"UTF-8\"";
                    context.Response.StatusCode = 401;
                    break;
                case AuthOutcome.Redirect:
                    context.Response.Redirect("/login?next=" + Uri.EscapeDataString(req.Path + req.QueryString));
                    break;
                case AuthOutcome.Forbidden:
                    context.Response.StatusCode = 403;
                    break;
                default:
                    context.Response.StatusCode = 401;
                    break;
            }
        }

        record LoginPayload(string user, string password);

        static string ClientOf(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "";

        // Only same-site paths: "/stats?x=1" yes, "//evil.com" and "http://..." no.
        internal static string SafeNext(string next) =>
            !string.IsNullOrEmpty(next) && next.StartsWith("/") && !next.StartsWith("//") && !next.StartsWith("/\\") && !next.StartsWith("/login") ? next : "/";

        static void MapAuthApi(WebApplication app)
        {
            app.MapGet("/login", () => Results.Content(Localize(Asset("login.html")), "text/html; charset=utf-8"));

            app.MapPost("/api/login", async (HttpContext context) =>
            {
                var web = Program.settings?.web;
                string client = ClientOf(context);
                if (LoginThrottle.IsBlocked(client))
                    return Results.Json(new { ok = false, error = "throttled" }, statusCode: 429);
                LoginPayload data;
                try { data = await context.Request.ReadFromJsonAsync<LoginPayload>(); }
                catch { return Results.BadRequest(); }
                var role = WebAuth.Authenticate(web, data?.user, data?.password);
                if (role == WebRole.None)
                {
                    LoginThrottle.Failed(client);
                    Program.Log("app", "", "", "Web login failed for \"" + data?.user + "\" from " + client);
                    return Results.Json(new { ok = false, error = "denied" }, statusCode: 401);
                }
                LoginThrottle.Succeeded(client);
                context.Response.Cookies.Append(WebAuth.CookieName, WebAuth.CreateSession(web, data.user, role, DateTime.UtcNow), new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                    Expires = DateTimeOffset.UtcNow.AddHours(Math.Max(1, web?.sessionhours ?? 168))
                });
                return Results.Ok(new { ok = true, role = role.ToString().ToLowerInvariant() });
            });

            app.MapPost("/api/logout", (HttpContext context) =>
            {
                context.Response.Cookies.Delete(WebAuth.CookieName);
                return Results.Ok(new { ok = true });
            });

            // What the page needs to know about the caller: show or hide Config and the logout button.
            app.MapGet("/api/whoami", (HttpContext context) =>
            {
                var web = Program.settings?.web;
                var role = WebRole.None;
                string user = null;
                if (!WebAuth.Required(web))
                    role = WebRole.Admin;
                else if (!(WebAuth.IsFormMode(web) && WebAuth.TryReadSession(web, context.Request.Cookies[WebAuth.CookieName], DateTime.UtcNow, out user, out role)))
                    role = WebAuth.AuthenticateBasic(web, context.Request.Headers.Authorization.ToString(), out user);
                return Results.Ok(new
                {
                    user,
                    role = role.ToString().ToLowerInvariant(),
                    auth = WebAuth.Required(web) ? (WebAuth.IsFormMode(web) ? "form" : "basic") : "none"
                });
            });

            // A link to a clip that opens without a login for a while: /api/sign/clip/<id>?hours=24
            app.MapGet("/api/sign/clip/{id}", (HttpContext context, string id, int? hours) =>
            {
                var web = Program.settings?.web;
                var expires = DateTime.UtcNow.AddHours(Math.Clamp(hours ?? 24, 1, 24 * 14));
                string path = "/api/clip/" + Uri.EscapeDataString(id) + "?token=" + WebAuth.SignClip(web, id, expires);
                string baseUrl = (web?.publicurl ?? "").TrimEnd('/');
                if (baseUrl == "") baseUrl = context.Request.Scheme + "://" + context.Request.Host;
                return Results.Ok(new { url = baseUrl + path, expires = new DateTimeOffset(expires).ToUnixTimeSeconds() });
            });
        }
    }
}
