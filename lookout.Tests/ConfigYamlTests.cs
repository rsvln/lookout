using Xunit;

namespace Lookout.Tests
{
    public class ConfigYamlTests
    {
        static readonly string Sample = """
            mqtt:
              # broker
              host: 10.0.0.1
              password: hunter2
            telegram:
              token: 'abc:def'
              chatids:
                - '-1001'
            options:
              timeoffset: 120
            """.Replace("\r\n", "\n");

        [Fact]
        public void Set_ReplacesAnExistingScalarAndKeepsTheComment()
        {
            string next = ConfigYaml.Set(Sample, "mqtt.host", "10.0.0.2");
            Assert.Contains("host: 10.0.0.2", next);
            Assert.Contains("# broker", next);
            Assert.Contains("hunter2", next);
        }

        [Fact]
        public void Set_InsertsAMissingKey()
        {
            string next = ConfigYaml.Set(Sample, "options.buttons", "true");
            Assert.Contains("buttons: true", next);
            Assert.Contains("timeoffset: 120", next);
        }

        [Fact]
        public void Set_ReplacesAScalarSequence()
        {
            string next = ConfigYaml.Set(Sample, "telegram.chatids", "-1, -2");
            Assert.Contains("[-1, -2]", next);
        }

        static readonly string Cameras = """
            frigate:
              host: 10.0.0.1
              cameras:
                - camera: frontdoor
                  clip: true
                - camera: yard
                  snapshot: true
            mqtt:
              host: 10.0.0.2
            """.Replace("\r\n", "\n");

        [Fact]
        public void Set_AddsACameraAndItsFields()
        {
            string next = ConfigYaml.Apply(Cameras, new Dictionary<string, string>
            {
                ["frigate.cameras[camera=garage].snapshot"] = "true",
                ["frigate.cameras[camera=garage].clip"] = "false",
            });
            Assert.Contains("camera: garage", next);
            Assert.Contains("snapshot: true", next);
            Assert.Contains("clip: false", next);
            Assert.Contains("camera: frontdoor", next);
            Assert.Contains("host: 10.0.0.2", next);
        }

        [Fact]
        public void Set_CreatesCamerasKeyWhenMissing()
        {
            string yaml = "frigate:\n  host: 10.0.0.1\nmqtt:\n  host: 10.0.0.2\n";
            string next = ConfigYaml.Set(yaml, "frigate.cameras[camera=gate].snapshot", "true");
            Assert.Contains("cameras:", next);
            Assert.Contains("camera: gate", next);
            Assert.Contains("snapshot: true", next);
            Assert.Contains("host: 10.0.0.1", next);
        }

        [Fact]
        public void Remove_DropsACamera()
        {
            string next = ConfigYaml.Remove(Cameras, "frigate.cameras[camera=frontdoor]");
            Assert.DoesNotContain("frontdoor", next);
            Assert.Contains("camera: yard", next);
            Assert.Contains("host: 10.0.0.2", next);
        }

        [Fact]
        public void Remove_LastCameraBecomesEmptyList()
        {
            string yaml = """
                frigate:
                  cameras:
                    - camera: only
                      clip: true
                mqtt:
                  host: 10.0.0.2
                """.Replace("\r\n", "\n");
            string next = ConfigYaml.Remove(yaml, "frigate.cameras[camera=only]");
            Assert.DoesNotContain("only", next);
            Assert.Contains("cameras: []", next);
            Assert.Contains("host: 10.0.0.2", next);
        }

        [Fact]
        public void Remove_MissingCameraIsUnchanged()
        {
            Assert.Equal(Cameras, ConfigYaml.Remove(Cameras, "frigate.cameras[camera=nope]"));
        }

        [Fact]
        public void Set_DoesNotNestAFlowSequence()
        {
            string yaml = "telegram:\n  chatids:\n    [-1001]\n";
            string next = ConfigYaml.Set(yaml, "telegram.chatids", "-1001, -1002");
            Assert.DoesNotContain("[[", next);
            Assert.DoesNotContain("]]", next);
            Assert.Contains("[-1001, -1002]", next);
            next = ConfigYaml.Set(next, "telegram.chatids", "-1001");
            Assert.DoesNotContain("]]", next);
            Assert.Contains("[-1001]", next);
        }

        [Fact]
        public void Remove_MiddleCameraWithNestedKeys()
        {
            string yaml = """
                frigate:
                  cameras:
                    - camera: dachacam01
                      clip: true
                      snapshot: true
                      cooldownperobject: false
                    - camera: home01
                      snapshot: true
                      clip: false
                      cooldownperobject: false
                    - camera: garage
                      snapshot: true
                      clip: false
                      cooldownperobject: false
                  recordingsoriginalpath: ""
                mqtt:
                  host: 10.0.0.2
                """.Replace("\r\n", "\n");
            string next = ConfigYaml.Remove(yaml, "frigate.cameras[camera=home01]");
            try
            {
                var s = new YamlDotNet.Serialization.DeserializerBuilder()
                    .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                    .Build()
                    .Deserialize<SettingsFile>(next);
                Assert.Equal(new[] { "dachacam01", "garage" }, s.frigate.cameras.Select(c => c.camera).ToArray());
            }
            catch (Exception ex)
            {
                Assert.Fail(ex.Message + "\n" + next);
            }
            Assert.DoesNotContain("home01", next);
            Assert.Contains("recordingsoriginalpath", next);
        }

        [Fact]
        public void Apply_FormSave_KeepsYamlValid()
        {
            string yaml = """
                frigate:
                  host: 127.0.0.1
                  port: 5000
                  clipspath: C:/tmp/clips
                  dbpath: C:/tmp/frigate.db
                  recordingspath: C:/tmp/rec
                  cameras:
                    - camera: dachacam01
                      clip: true
                      snapshot: true
                    - camera: home01
                mqtt:
                  host: 127.0.0.1
                  port: 1883
                telegram:
                  token: "123456:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw"
                  chatids:
                    - "-1001"
                options:
                  timeoffset: 180
                logger:
                  file: false
                  console: true
                """.Replace("\r\n", "\n");
            var fields = new Dictionary<string, string>
            {
                ["frigate.recordingsoriginalpath"] = "",
                ["frigate.cameras[camera=dachacam01].gif"] = "false",
                ["frigate.cameras[camera=dachacam01].topic"] = "reviews",
                ["frigate.cameras[camera=home01].snapshot"] = "true",
                ["frigate.cameras[camera=home01].clip"] = "false",
                ["frigate.cameras[camera=garage].snapshot"] = "true",
                ["frigate.cameras[camera=garage].clip"] = "false",
                ["telegram.chatids"] = "-1001",
            };
            string last = yaml;
            foreach (var kv in fields)
            {
                last = ConfigYaml.Set(last, kv.Key, kv.Value);
                try
                {
                    new YamlDotNet.Serialization.DeserializerBuilder()
                        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                        .Build()
                        .Deserialize<SettingsFile>(last);
                }
                catch (Exception ex)
                {
                    Assert.Fail("After " + kv.Key + ": " + ex.Message + "\n" + last);
                }
            }
            Assert.Contains("camera: garage", last);
        }

        [Fact]
        public void Set_AddsNotifierWhenMissing()
        {
            string next = ConfigYaml.Apply(Sample, new Dictionary<string, string>
            {
                ["notifiers[0].type"] = "ntfy",
                ["notifiers[0].url"] = "https://ntfy.sh/alerts",
                ["notifiers[0].attach"] = "true",
            });
            Assert.Contains("notifiers:", next);
            Assert.Contains("type: ntfy", next);
            Assert.Contains("https://ntfy.sh/alerts", next);
            Assert.Contains("host: 10.0.0.1", next);
        }

        [Fact]
        public void Set_GrowsNotifierListByIndex()
        {
            string yaml = Sample + "\nnotifiers:\n  - type: ntfy\n    url: https://ntfy.sh/a\n";
            string next = ConfigYaml.Apply(yaml, new Dictionary<string, string>
            {
                ["notifiers[1].type"] = "discord",
                ["notifiers[1].url"] = "https://discord.example/hook",
            });
            Assert.Contains("type: ntfy", next);
            Assert.Contains("type: discord", next);
            Assert.Contains("https://discord.example/hook", next);
        }

        [Fact]
        public void Set_InsertsTelegramNotifierChatidsAsSequence()
        {
            string next = ConfigYaml.Apply(Sample, new Dictionary<string, string>
            {
                ["notifiers[0].type"] = "telegram",
                ["notifiers[0].chatids"] = "-1001, -1002",
            });
            Assert.Contains("type: telegram", next);
            Assert.Contains("[-1001, -1002]", next);
        }

        [Fact]
        public void Remove_DropsNotifierByIndex()
        {
            string yaml = Sample + "\nnotifiers:\n  - type: ntfy\n    url: https://ntfy.sh/a\n  - type: webhook\n    url: http://127.0.0.1/hook\n";
            string next = ConfigYaml.Remove(yaml, "notifiers[0]");
            Assert.DoesNotContain("ntfy", next);
            Assert.Contains("type: webhook", next);
            next = ConfigYaml.Remove(next, "notifiers[0]");
            Assert.Contains("notifiers: []", next);
        }

        [Fact]
        public void Snapshot_AlwaysHasNotifiersGroup()
        {
            Program.appLocation = AppContext.BaseDirectory;
            L10n.Load("en", "en", "en");
            string json = System.Text.Json.JsonSerializer.Serialize(SettingsForm.Snapshot(new SettingsFile()));
            Assert.Contains("\"id\":\"notifiers\"", json);
            Assert.Contains("\"title\":\"Notifiers\"", json);
            Assert.Contains("\"notifiers\":[]", json);
            Assert.Contains("notifierTemplates", json);
        }

        [Fact]
        public void Snapshot_ExposesExistingNotifiers()
        {
            Program.appLocation = AppContext.BaseDirectory;
            L10n.Load("en", "en", "en");
            var s = new SettingsFile
            {
                notifiers = new List<NotifierSettings>
                {
                    new NotifierSettings { type = "discord", url = "https://discord.example/hook" }
                }
            };
            string json = System.Text.Json.JsonSerializer.Serialize(SettingsForm.Snapshot(s));
            Assert.Contains("notifiers[0].url", json);
            Assert.Contains("https://discord.example/hook", json);
            Assert.Contains("\"type\":\"discord\"", json);
        }
    }
}
