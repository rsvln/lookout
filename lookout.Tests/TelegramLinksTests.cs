using Xunit;

namespace Lookout.Tests
{
    [Collection(ProgramStateCollection.Name)]
    public class TelegramLinksTests
    {
        const string Review = "1792234603.114642-machwt";
        const string Ev1 = "1792234601.902253-h7g2nf";
        const string Ev2 = "1792234610.5-abc";

        static string Caption =>
            "Review: \t" + Review + "\nCamera: dacha<cam>&9\nEvents: " + Ev1 + ", " + Ev2;

        public TelegramLinksTests() => TestEnv.Init();

        static void Links(bool enabled, string target = "frigate", string frigateurl = "", string lookouturl = "") =>
            Program.settings.telegram.links = new TelegramLinkSettings
            {
                enabled = enabled, target = target, frigateurl = frigateurl, lookouturl = lookouturl
            };

        [Fact]
        public void Off_GivesEscapedPlainText()
        {
            Links(false, "frigate", "https://nvr.example.org");
            string html = TelegramLinks.Review(Caption, Review, new[] { Ev1, Ev2 });
            Assert.DoesNotContain("<a ", html);
            Assert.Contains("dacha&lt;cam&gt;&amp;9", html);
            Assert.Contains(Review, html);
        }

        [Fact]
        public void Frigate_UsesFrigateUrl()
        {
            Links(true, "frigate", "https://nvr.example.org/", "https://lookout.example.org");
            string html = TelegramLinks.Review(Caption, Review, new[] { Ev1, Ev2 });
            Assert.Contains($"<a href=\"https://nvr.example.org/review?id={Review}\">{Review}</a>", html);
            Assert.Contains($"<a href=\"https://nvr.example.org/explore?event_id={Ev1}\">{Ev1}</a>", html);
            Assert.Contains($"<a href=\"https://nvr.example.org/explore?event_id={Ev2}\">{Ev2}</a>", html);
        }

        [Fact]
        public void Lookout_UsesLookoutUrl()
        {
            Links(true, "lookout", "https://nvr.example.org", "https://lookout.example.org/");
            string html = TelegramLinks.Review(Caption, Review, new[] { Ev1 });
            Assert.Contains($"<a href=\"https://lookout.example.org/search?q={Review}\">{Review}</a>", html);
            Assert.Contains($"<a href=\"https://lookout.example.org/event/{Ev1}\">{Ev1}</a>", html);
            Assert.DoesNotContain($">{Ev2}</a>", html);   // not in the list of linked ids
        }

        [Theory]
        [InlineData("frigate", "", "https://lookout.example.org")]   // the other target's address doesn't count
        [InlineData("lookout", "https://nvr.example.org", "")]
        [InlineData("frigate", "  ", "")]
        public void WithoutTheTargetsUrl_NoLinks_AndNoInternalAddressIsGuessed(string target, string frigateurl, string lookouturl)
        {
            Links(true, target, frigateurl, lookouturl);
            Program.settings.web = new WebSettings { publicurl = "http://lab3:8888" };
            Assert.Null(TelegramLinks.EventUrl(Ev1));
            Assert.Null(TelegramLinks.ReviewUrl(Review));
            Assert.DoesNotContain("<a ", TelegramLinks.Review(Caption, Review, new[] { Ev1 }));
            Program.settings.web = null;
        }

        [Fact]
        public void EveryOccurrence_IsLinkedOnce_AndNeverInsideAnotherId()
        {
            Links(true, "frigate", "https://nvr.example.org");
            // A video caption repeats the id with a part number; another id starting the same way must stay plain.
            string html = TelegramLinks.Event(Ev1 + "[2] Video\n" + Ev1 + "\n" + Ev1 + "x", Ev1);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "<a href=").Count);
            Assert.Contains(Ev1 + "x", html.Replace("</a>", ""));
            Assert.DoesNotContain(">" + Ev1 + "</a>x", html);
        }

        [Fact]
        public void Append_KeepsLinks_AndFitsTheLimitByVisibleText()
        {
            Links(true, "frigate", "https://nvr.example.org");
            string caption = TelegramLinks.Event("Event: " + Ev1, Ev1);
            string result = TelegramLinks.Append(caption, new string('a', 2000) + "<b>");
            Assert.StartsWith(caption + "\n\n", result);
            Assert.Equal(1024, TelegramLinks.VisibleLength(result));
            Assert.EndsWith("...", result);

            Assert.Equal(caption + "\n\nAI: a &lt; b", TelegramLinks.Append(caption, "AI: a < b"));
        }
    }
}
