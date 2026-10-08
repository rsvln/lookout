using Xunit;

namespace Lookout.Tests
{
    [Collection(ProgramStateCollection.Name)]
    public class L10nTests
    {
        public L10nTests() => TestEnv.Init();

        [Fact]
        public void English_ReturnsStringsWithArguments()
        {
            Assert.Equal("24 h", L10n.Tg.T("period.hours", 24));
            Assert.Equal("<b>Version:</b> 1.2.3", L10n.Tg.T("tg.help.version", "1.2.3"));
        }

        [Fact]
        public void Russian_IsLoadedFromItsFile()
        {
            L10n.Load("ru", "ru", "en");
            Assert.Equal("ru", L10n.Web.Locale);
            Assert.Equal("24 ч", L10n.Tg.T("period.hours", 24));
            Assert.Equal("en", L10n.Ai.Locale);
        }

        [Fact]
        public void ChineseAndSpanish_AreLoadedFromTheirFiles()
        {
            L10n.Load("zh", "zh", "zh");
            Assert.Equal("zh", L10n.Web.Locale);
            Assert.Equal("人", L10n.Tg.Label("person"));
            L10n.Load("es", "es", "es");
            Assert.Equal("es", L10n.Web.Locale);
            Assert.Equal("persona", L10n.Tg.Label("person"));
        }

        [Fact]
        public void UnknownLocale_FallsBackToEnglish()
        {
            L10n.Load("xx", "xx", "xx");
            Assert.Equal("en", L10n.Web.Locale);
            Assert.Equal("24 h", L10n.Web.T("period.hours", 24));
        }

        [Fact]
        public void BlankLocale_MeansEnglish()
        {
            L10n.Load("  ", null, "EN");
            Assert.Equal("en", L10n.Web.Locale);
            Assert.Equal("en", L10n.Tg.Locale);
            Assert.Equal("en", L10n.Ai.Locale);
        }

        [Fact]
        public void MissingKey_IsReturnedAsIs()
        {
            Assert.Equal("no.such.key", L10n.Tg.T("no.such.key"));
            Assert.False(L10n.Tg.Has("no.such.key"));
        }

        [Fact]
        public void KeyMissingInLocale_FallsBackToEnglishText()
        {
            var en = new Dictionary<string, string> { ["a"] = "english a", ["b"] = "english b" };
            var xx = new Dictionary<string, string> { ["a"] = "xx a" };
            var s = new Strings("xx", xx, en);
            Assert.Equal("xx a", s.T("a"));
            Assert.Equal("english b", s.T("b"));
        }

        [Fact]
        public void Label_UsesTranslationOrTheLabelItself()
        {
            L10n.Load("ru", "ru", "ru");
            Assert.Equal("человек", L10n.Tg.Label("person"));
            Assert.Equal("zebra", L10n.Tg.Label("zebra"));
        }

        [Fact]
        public void LabelFromName_UnderstandsEveryLanguage()
        {
            L10n.Load("en", "en", "en");
            Assert.Equal("person", L10n.LabelFromName("человек"));
            Assert.Equal("person", L10n.LabelFromName("PERSON"));
            Assert.Null(L10n.LabelFromName("nothing"));
        }

        [Fact]
        public void Export_ContainsOnlyRequestedPrefixes_WithFallback()
        {
            var export = L10n.Web.Export("web.", "label.");
            Assert.All(export.Keys, k => Assert.True(k.StartsWith("web.") || k.StartsWith("label.")));
            Assert.Contains("web.lang", export.Keys);
            Assert.Contains("label.person", export.Keys);
            Assert.DoesNotContain("period.hours", export.Keys);
        }

        [Fact]
        public void EveryLocale_HasTheSameKeysAsEnglish()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "locales");
            var en = Keys(Path.Combine(dir, "en.json"));
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var keys = Keys(file);
                string name = Path.GetFileName(file);
                Assert.True(en.Except(keys).Count() == 0, name + " lacks: " + string.Join(", ", en.Except(keys)));
                Assert.True(keys.Except(en).Count() == 0, name + " has extra keys: " + string.Join(", ", keys.Except(en)));
            }
        }

        static HashSet<string> Keys(string path) =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)).Keys.ToHashSet();
    }
}
