using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lookout.Tests
{
    public class ConfigTests
    {
        static readonly string Minimal = """
            frigate:
              host: 192.168.1.10
              port: 5000
              clipspath: /srv/frigate/clips
              dbpath: /srv/frigate/config/frigate.db
              cameras:
                - camera: frontdoor
                  clip: true
                  snapshottrigger: new
                  objects:
                    - label: person
                      percent: 60
            mqtt:
              host: 10.0.0.1
              port: 1883
            telegram:
              token: abc
              chatids:
                - '-100123'
              clipsizecheck: 2147483648
              clipsizesplit: 2000000000
            options:
              timeoffset: 120
            logger:
              file: true
              console: false
            """.Replace("\r\n", "\n");

        static SettingsFile Parse(string yaml) => new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Deserialize<SettingsFile>(yaml);

        [Fact]
        public void Parses_ValuesAndDefaults()
        {
            var s = Parse(Minimal);

            Assert.Equal("192.168.1.10", s.frigate.host);
            Assert.Equal(5000, s.frigate.port);
            var cam = Assert.Single(s.frigate.cameras);
            Assert.Equal("frontdoor", cam.camera);
            Assert.True(cam.clip);
            Assert.True(cam.snapshot);                 // default
            Assert.Equal("reviews", cam.topic);        // default
            Assert.Equal("new", cam.snapshottrigger);
            Assert.Equal(new[] { "detection", "alert" }, cam.severity);
            Assert.Equal(60, Assert.Single(cam.objects).percent);

            Assert.Equal("frigate/events", s.mqtt.eventstopic);
            Assert.Equal(10, s.telegram.mediagrouplimit);
            Assert.Equal(2147483648L, s.telegram.clipsizecheck);
            Assert.Equal(120, s.options.timeoffset);
            Assert.Equal(300, s.options.timeout);
            Assert.True(s.logger.file);
            Assert.Null(s.ai);
            Assert.Null(s.web);
            Assert.Empty(s.notifiers);
        }

        [Fact]
        public void Ai_ProviderDefaultsToOllama()
        {
            var s = Parse(Minimal + "\nai:\n  url: http://127.0.0.1:11434\n  model: llava\n");
            Assert.Equal("ollama", s.ai.provider);
        }

        [Fact]
        public void Notifiers_ParsesNtfyAndWebhook()
        {
            var s = Parse(Minimal + "\nnotifiers:\n  - type: ntfy\n    url: https://ntfy.sh/alerts\n  - type: webhook\n    url: http://127.0.0.1/hook\n");
            Assert.Equal(new[] { "ntfy", "webhook" }, s.notifiers.Select(n => n.type));
            Assert.True(s.notifiers[0].attach);
        }

        [Fact]
        public void Locale_ScalarSetsEveryArea()
        {
            var s = Parse(Minimal.Replace("options:\n  timeoffset: 120", "options:\n  locale: ru\n  timeoffset: 120"));
            Assert.Equal("ru", s.options.locale.web);
            Assert.Equal("ru", s.options.locale.telegram);
            Assert.Equal("ru", s.options.locale.ai);
        }

        [Fact]
        public void Locale_PerAreaLeavesTheRestEnglish()
        {
            var s = Parse(Minimal.Replace("options:\n  timeoffset: 120", "options:\n  locale:\n    telegram: ru\n  timeoffset: 120"));
            Assert.Equal("en", s.options.locale.web);
            Assert.Equal("ru", s.options.locale.telegram);
            Assert.Equal("en", s.options.locale.ai);
            Assert.False(s.options.locale.aiExplicit);
        }

        [Fact]
        public void Locale_AiExplicitIsRemembered()
        {
            var s = Parse(Minimal.Replace("options:\n  timeoffset: 120", "options:\n  locale:\n    ai: uk\n  timeoffset: 120"));
            Assert.Equal("uk", s.options.locale.ai);
            Assert.True(s.options.locale.aiExplicit);
        }

        [Fact]
        public void WebUi_ParseSettings_AcceptsValidConfig()
        {
            Assert.Equal("10.0.0.1", WebUi.ParseSettings(Minimal).mqtt.host);
        }

        [Fact]
        public void WebUi_ParseSettings_ReportsMissingSections()
        {
            var ex = Assert.Throws<InvalidDataException>(() => WebUi.ParseSettings("frigate:\n  host: x\n"));
            Assert.Contains("mqtt", ex.Message);
            Assert.Contains("telegram", ex.Message);
            Assert.Contains("options", ex.Message);
            Assert.Contains("logger", ex.Message);
            Assert.DoesNotContain("frigate", ex.Message);
        }

        [Fact]
        public void WebUi_ParseSettings_RejectsBrokenYaml()
        {
            Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => WebUi.ParseSettings("frigate:\n  port: [unclosed\n"));
        }
    }
}
