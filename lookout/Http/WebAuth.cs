using System.Security.Cryptography;
using System.Text;

namespace Lookout
{
    public enum WebRole { None, Viewer, Admin }

    public enum AuthOutcome
    {
        Allow,
        Challenge,      // 401 with WWW-Authenticate: the browser asks for a name and password
        Unauthorized,   // 401 without it
        Redirect,       // to the login page
        Forbidden,      // logged in, but the role is too low
    }

    public class AuthRequest
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "/";
        public bool WantsHtml { get; set; }
        public string AuthorizationHeader { get; set; }
        public string SessionCookie { get; set; }
        public string Token { get; set; }
    }

    public class AuthDecision
    {
        public AuthOutcome Outcome { get; set; }
        public WebRole Role { get; set; }
        public string User { get; set; }
    }

    // Web UI access: accounts and roles, session cookies, signed clip links.
    //   - web.user / web.password: the admin, as before (alone, they keep the old behaviour: HTTP Basic for everything);
    //   - web.users: more accounts, "viewer" (default, no Config / Apply) or "admin";
    //   - web.auth: form adds the login page and a signed session cookie; Basic credentials still work next to it.
    public static class WebAuth
    {
        public const string CookieName = "lookout_session";

        // Reachable without a login: probes, the login page itself and what a browser needs to install the app.
        static readonly string[] OpenPaths = { "/health", "/login", "/api/login", "/api/logout", "/api/whoami", "/manifest.webmanifest", "/sw.js", "/favicon.svg" };

        static readonly byte[] processKey = RandomNumberGenerator.GetBytes(32);

        public static bool IsFormMode(WebSettings w) => string.Equals(w?.auth, "form", StringComparison.OrdinalIgnoreCase);

        public static bool IsOpenPath(string path) =>
            !string.IsNullOrEmpty(path)
            && (OpenPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
                || path.StartsWith("/icon/", StringComparison.OrdinalIgnoreCase));

        static IEnumerable<(string user, string password, WebRole role)> Accounts(WebSettings w)
        {
            if (w == null) yield break;
            if (!string.IsNullOrEmpty(w.user) && !string.IsNullOrEmpty(w.password))
                yield return (w.user, w.password, WebRole.Admin);
            foreach (var u in w.users ?? new List<WebUser>())
                if (!string.IsNullOrEmpty(u.user) && !string.IsNullOrEmpty(u.password))
                    yield return (u.user, u.password, string.Equals(u.role, "admin", StringComparison.OrdinalIgnoreCase) ? WebRole.Admin : WebRole.Viewer);
        }

        // False: nobody is asked for anything (no accounts configured).
        public static bool Required(WebSettings w) => Accounts(w).Any();

        static bool Same(string a, string b) =>
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

        public static WebRole Authenticate(WebSettings w, string user, string password)
        {
            user ??= "";
            password ??= "";
            var role = WebRole.None;
            foreach (var a in Accounts(w))     // no early exit: the time does not tell which account matched
                if (Same(user, a.user) & Same(password, a.password) && a.role > role)
                    role = a.role;
            return role;
        }

        // "Basic base64(user:password)" -> role.
        public static WebRole AuthenticateBasic(WebSettings w, string header, out string user)
        {
            user = null;
            if (header == null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return WebRole.None;
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim())); }
            catch (FormatException) { return WebRole.None; }
            int colon = decoded.IndexOf(':');
            if (colon < 0) return WebRole.None;
            user = decoded.Substring(0, colon);
            return Authenticate(w, user, decoded.Substring(colon + 1));
        }

        // ---- signing ----------------------------------------------------------------------------------------------

        static byte[] Key(WebSettings w)
        {
            var accounts = Accounts(w).Select(a => a.user + "\n" + a.password).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (string.IsNullOrEmpty(w?.secret) && accounts.Count == 0)
                return processKey;
            return SHA256.HashData(Encoding.UTF8.GetBytes("lookout-web\n" + (w.secret ?? "") + "\n" + string.Join("\n\n", accounts)));
        }

        static string Sign(WebSettings w, string data) =>
            Convert.ToHexString(HMACSHA256.HashData(Key(w), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

        static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        static bool SameText(string a, string b) => a != null && b != null && Same(a, b);

        public static string CreateSession(WebSettings w, string user, WebRole role, DateTime utcNow)
        {
            long exp = Unix(utcNow.AddHours(Math.Max(1, w?.sessionhours ?? 168)));
            string u = Convert.ToBase64String(Encoding.UTF8.GetBytes(user)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            string body = u + "." + (int)role + "." + exp;
            return body + "." + Sign(w, "s|" + body);
        }

        public static bool TryReadSession(WebSettings w, string cookie, DateTime utcNow, out string user, out WebRole role)
        {
            user = null;
            role = WebRole.None;
            var parts = (cookie ?? "").Split('.');
            if (parts.Length != 4 || !long.TryParse(parts[2], out long exp) || !int.TryParse(parts[1], out int r) || r < 1 || r > 2)
                return false;
            if (!SameText(parts[3], Sign(w, "s|" + string.Join(".", parts.Take(3)))) || exp < Unix(utcNow))
                return false;
            try
            {
                string b64 = parts[0].Replace('-', '+').Replace('_', '/');
                user = Encoding.UTF8.GetString(Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4)));
            }
            catch (FormatException) { return false; }
            // The account must still exist with at least that role (it may have been removed or demoted in the config).
            string name = user;
            var current = Accounts(w).Where(a => a.user == name).Select(a => a.role).DefaultIfEmpty(WebRole.None).Max();
            role = (WebRole)Math.Min(r, (int)current);
            return role != WebRole.None;
        }

        // /api/clip/{id}?token=<expires>.<signature>: a link that works without a login until it expires.
        public static string SignClip(WebSettings w, string id, DateTime expiresUtc)
        {
            long exp = Unix(expiresUtc);
            return exp + "." + Sign(w, "c|" + id + "|" + exp);
        }

        public static bool VerifyClip(WebSettings w, string id, string token, DateTime utcNow)
        {
            var parts = (token ?? "").Split('.');
            return parts.Length == 2 && long.TryParse(parts[0], out long exp) && exp >= Unix(utcNow)
                   && SameText(parts[1], Sign(w, "c|" + id + "|" + exp));
        }

        // ---- the decision -------------------------------------------------------------------------------------------

        static bool NeedsAdmin(AuthRequest r) =>
            r.Path.StartsWith("/api/config", StringComparison.OrdinalIgnoreCase)
            || r.Path.Equals("/api/apply", StringComparison.OrdinalIgnoreCase)
            || r.Path.StartsWith("/api/settings", StringComparison.OrdinalIgnoreCase);

        public static AuthDecision Decide(WebSettings w, AuthRequest r, DateTime utcNow)
        {
            if (!Required(w))
                return new AuthDecision { Outcome = AuthOutcome.Allow, Role = WebRole.Admin };
            if (IsOpenPath(r.Path))
                return new AuthDecision { Outcome = AuthOutcome.Allow, Role = WebRole.None };

            const string clipPrefix = "/api/clip/";
            if (r.Token != null && r.Path.StartsWith(clipPrefix, StringComparison.OrdinalIgnoreCase)
                && VerifyClip(w, Uri.UnescapeDataString(r.Path.Substring(clipPrefix.Length)), r.Token, utcNow))
                return new AuthDecision { Outcome = AuthOutcome.Allow, Role = WebRole.Viewer };

            string user = null;
            WebRole role = WebRole.None;
            if (IsFormMode(w) && TryReadSession(w, r.SessionCookie, utcNow, out user, out role)) { }
            else role = AuthenticateBasic(w, r.AuthorizationHeader, out user);

            if (role == WebRole.None)
            {
                if (!IsFormMode(w))
                    return new AuthDecision { Outcome = AuthOutcome.Challenge };
                bool page = r.WantsHtml && r.Method == "GET" && !r.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
                return new AuthDecision { Outcome = page ? AuthOutcome.Redirect : AuthOutcome.Unauthorized };
            }

            if (NeedsAdmin(r) && role != WebRole.Admin)
                return new AuthDecision { Outcome = AuthOutcome.Forbidden, Role = role, User = user };
            return new AuthDecision { Outcome = AuthOutcome.Allow, Role = role, User = user };
        }
    }

    // Wrong passwords: after 5 in 5 minutes from one address that address waits.
    public static class LoginThrottle
    {
        public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
        const int MaxFailures = 5;
        static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
        static readonly Dictionary<string, List<DateTime>> failures = new Dictionary<string, List<DateTime>>();
        static readonly object sync = new object();

        public static bool IsBlocked(string client)
        {
            lock (sync)
            {
                if (!failures.TryGetValue(client ?? "", out var list)) return false;
                list.RemoveAll(t => Clock() - t > Window);
                return list.Count >= MaxFailures;
            }
        }

        public static void Failed(string client)
        {
            lock (sync)
            {
                if (!failures.TryGetValue(client ?? "", out var list))
                    failures[client ?? ""] = list = new List<DateTime>();
                list.Add(Clock());
            }
        }

        public static void Succeeded(string client)
        {
            lock (sync) failures.Remove(client ?? "");
        }

        public static void Reset()
        {
            lock (sync) failures.Clear();
        }
    }
}
