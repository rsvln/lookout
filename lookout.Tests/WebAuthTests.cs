using System.Text;
using Xunit;

namespace Lookout.Tests
{
    public class WebAuthTests : IDisposable
    {
        static readonly DateTime Now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        static readonly WebSettings Admin = new WebSettings { user = "admin", password = "secret" };
        static readonly WebSettings Form = new WebSettings { user = "admin", password = "secret", auth = "form", sessionhours = 24 };
        static readonly WebSettings Roles = new WebSettings
        {
            user = "admin", password = "secret", auth = "form",
            users = new List<WebUser> { new WebUser { user = "bob", password = "viewer", role = "viewer" } }
        };

        public WebAuthTests() => LoginThrottle.Reset();
        public void Dispose()
        {
            LoginThrottle.Reset();
            LoginThrottle.Clock = () => DateTime.UtcNow;
        }

        static string Basic(string user, string password) =>
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

        [Fact]
        public void NoAccounts_AllowsEveryoneAsAdmin()
        {
            var d = WebAuth.Decide(new WebSettings(), new AuthRequest { Path = "/api/config" }, Now);
            Assert.Equal(AuthOutcome.Allow, d.Outcome);
            Assert.Equal(WebRole.Admin, d.Role);
        }

        [Fact]
        public void Basic_WrongPassword_Challenges()
        {
            var d = WebAuth.Decide(Admin, new AuthRequest { Path = "/", AuthorizationHeader = Basic("admin", "nope") }, Now);
            Assert.Equal(AuthOutcome.Challenge, d.Outcome);
        }

        [Fact]
        public void Basic_RightPassword_AllowsAdmin()
        {
            var d = WebAuth.Decide(Admin, new AuthRequest { Path = "/api/config", AuthorizationHeader = Basic("admin", "secret") }, Now);
            Assert.Equal(AuthOutcome.Allow, d.Outcome);
            Assert.Equal(WebRole.Admin, d.Role);
            Assert.Equal("admin", d.User);
        }

        [Fact]
        public void Health_IsOpen()
        {
            var d = WebAuth.Decide(Admin, new AuthRequest { Path = "/health" }, Now);
            Assert.Equal(AuthOutcome.Allow, d.Outcome);
        }

        [Fact]
        public void Form_HtmlGet_RedirectsToLogin()
        {
            var d = WebAuth.Decide(Form, new AuthRequest { Path = "/stats", Method = "GET", WantsHtml = true }, Now);
            Assert.Equal(AuthOutcome.Redirect, d.Outcome);
        }

        [Fact]
        public void Form_ApiWithoutSession_IsUnauthorized()
        {
            var d = WebAuth.Decide(Form, new AuthRequest { Path = "/api/stat" }, Now);
            Assert.Equal(AuthOutcome.Unauthorized, d.Outcome);
        }

        [Fact]
        public void Form_BasicStillWorks()
        {
            var d = WebAuth.Decide(Form, new AuthRequest { Path = "/metrics", AuthorizationHeader = Basic("admin", "secret") }, Now);
            Assert.Equal(AuthOutcome.Allow, d.Outcome);
        }

        [Fact]
        public void Session_RoundTrip()
        {
            string cookie = WebAuth.CreateSession(Form, "admin", WebRole.Admin, Now);
            Assert.True(WebAuth.TryReadSession(Form, cookie, Now.AddHours(1), out string user, out var role));
            Assert.Equal("admin", user);
            Assert.Equal(WebRole.Admin, role);
            Assert.False(WebAuth.TryReadSession(Form, cookie, Now.AddDays(2), out _, out _));
        }

        [Fact]
        public void Viewer_CannotTouchConfig()
        {
            string cookie = WebAuth.CreateSession(Roles, "bob", WebRole.Viewer, Now);
            var d = WebAuth.Decide(Roles, new AuthRequest { Path = "/api/config", SessionCookie = cookie }, Now);
            Assert.Equal(AuthOutcome.Forbidden, d.Outcome);
            Assert.Equal(WebRole.Viewer, d.Role);

            var ok = WebAuth.Decide(Roles, new AuthRequest { Path = "/api/stat", SessionCookie = cookie }, Now);
            Assert.Equal(AuthOutcome.Allow, ok.Outcome);
        }

        [Fact]
        public void SignedClip_OpensWithoutLoginUntilItExpires()
        {
            string token = WebAuth.SignClip(Admin, "evt-1", Now.AddHours(2));
            var d = WebAuth.Decide(Admin, new AuthRequest { Path = "/api/clip/evt-1", Token = token }, Now);
            Assert.Equal(AuthOutcome.Allow, d.Outcome);
            Assert.False(WebAuth.VerifyClip(Admin, "evt-1", token, Now.AddHours(3)));
            Assert.False(WebAuth.VerifyClip(Admin, "other", token, Now));
        }

        [Theory]
        [InlineData("/stats", "/stats")]
        [InlineData("/stats?x=1", "/stats?x=1")]
        [InlineData("//evil", "/")]
        [InlineData("http://x", "/")]
        [InlineData("/login?next=/", "/")]
        [InlineData(null, "/")]
        public void SafeNext(string next, string expected) => Assert.Equal(expected, WebUi.SafeNext(next));

        [Fact]
        public void Throttle_BlocksAfterFiveFailures()
        {
            var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            LoginThrottle.Clock = () => t;
            for (int i = 0; i < 5; i++) LoginThrottle.Failed("1.2.3.4");
            Assert.True(LoginThrottle.IsBlocked("1.2.3.4"));
            Assert.False(LoginThrottle.IsBlocked("other"));
            LoginThrottle.Succeeded("1.2.3.4");
            Assert.False(LoginThrottle.IsBlocked("1.2.3.4"));
        }
    }
}
