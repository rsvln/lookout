using Xunit;

namespace Lookout.Tests
{
    // The Config form templates (lookout/forms) against the settings model.
    [Collection(ProgramStateCollection.Name)]
    public class SettingsTemplatesTests
    {
        public SettingsTemplatesTests()
        {
            TestEnv.Init();
            SettingsForm.Load(reload: true);
        }

        static IEnumerable<string> TemplateLeaves() =>
            SettingsForm.Templates().Values.SelectMany(SettingsForm.TemplatePaths).Select(p => SettingsForm.Normalize(p.path));

        [Fact]
        public void EveryModelSetting_IsInATemplate_OrDeliberatelyHidden()
        {
            var placed = TemplateLeaves().ToHashSet();
            var hidden = SettingsForm.Tree().hidden.Keys.ToList();
            var missing = SettingsForm.ModelLeaves()
                .Where(leaf => !placed.Contains(leaf))
                .Where(leaf => !hidden.Any(h => leaf == h || leaf.StartsWith(h + ".") || leaf.StartsWith(h + "[]")))
                .ToList();
            Assert.True(missing.Count == 0, "Not in any form template nor in tree.json hidden: " + string.Join(", ", missing));
        }

        [Fact]
        public void HiddenEntries_AreRealSettings()
        {
            var leaves = SettingsForm.ModelLeaves().ToList();
            foreach (var h in SettingsForm.Tree().hidden.Keys)
                Assert.True(leaves.Any(l => l == h || l.StartsWith(h + ".") || l.StartsWith(h + "[]")), "tree.json hidden: " + h);
        }

        [Fact]
        public void NoPathTwiceInATemplate_AndEnabledByPointsToACheckbox()
        {
            foreach (var (name, tmpl) in SettingsForm.Templates())
            {
                var paths = SettingsForm.TemplatePaths(tmpl).Select(p => p.path).ToList();
                Assert.True(paths.Count == paths.Distinct().Count(), name + ": a path is used twice");
                foreach (var (path, item) in SettingsForm.TemplatePaths(tmpl).Where(p => p.item.enabledBy != null))
                {
                    string target = path.Substring(0, path.Length - item.path.Length) + item.enabledBy;
                    Assert.True(SettingsForm.ResolveType(target) == typeof(bool), $"{name}: enabledBy of {path} is not a checkbox");
                }
            }
        }

        [Fact]
        public void TreeNodes_HaveTemplates()
        {
            var names = SettingsForm.Templates().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            void Check(IEnumerable<SettingsForm.TreeNodeT> nodes)
            {
                foreach (var n in nodes)
                {
                    Assert.Contains(n.node, names);
                    if (n.children.ValueKind == System.Text.Json.JsonValueKind.Array)
                        Check(System.Text.Json.JsonSerializer.Deserialize<List<SettingsForm.TreeNodeT>>(n.children.GetRawText(),
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
                }
            }
            Check(SettingsForm.Tree().nodes);
            Assert.Contains("camera", names);
            foreach (var t in SettingsForm.NotifierTypes) Assert.Contains("notifier-" + t, names);
        }

        const string Sample = "frigate:\n  host: 10.0.0.1\n  port: 5000\n  cameras:\n    - camera: yard\n      clip: true\n      objects:\n        - label: person\n          percent: 70\nmqtt:\n  host: h\ntelegram:\n  token: 'a:b'\n  chatids: ['-1']\noptions:\n  timeoffset: 180\n  locale: ru\nlogger:\n  file: false\nnotifiers:\n  - type: discord\n    url: https://discord.example/hook\n";

        // Every field of every pane, as the page reads them back.
        static Dictionary<string, string> FormValues(SettingsFile s)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(SettingsForm.Snapshot(s));
            var values = new Dictionary<string, string>();
            void Walk(System.Text.Json.JsonElement e)
            {
                if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (e.TryGetProperty("path", out var p) && e.TryGetProperty("value", out var v) && !p.GetString().Contains('{'))
                        values[p.GetString()] = v.GetString();
                    foreach (var prop in e.EnumerateObject()) if (prop.Name != "notifierTemplates" && prop.Name != "cameraTemplate") Walk(prop.Value);
                }
                else if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var x in e.EnumerateArray()) Walk(x);
            }
            Walk(System.Text.Json.JsonDocument.Parse(json).RootElement);
            return values;
        }

        [Fact]
        public void Snapshot_ShowsTheConfigValues()
        {
            var v = FormValues(WebUi.ParseSettings(Sample));
            Assert.Equal("10.0.0.1", v["frigate.host"]);
            Assert.Equal("true", v["frigate.cameras[camera=yard].clip"]);
            Assert.Equal("person:70", v["frigate.cameras[camera=yard].objects"]);
            Assert.Equal("", v["frigate.cameras[camera=yard].quiet.mode"]);   // follows options.quiet
            Assert.Equal("ru", v["options.locale.telegram"]);
            Assert.Equal("https://discord.example/hook", v["notifiers[0].url"]);
            Assert.Equal("silent", v["options.quiet.mode"]);                // default when quiet hours are not set
        }

        [Fact]
        public void LocaleScalar_BecomesABlock_KeepingTheOtherAreas()
        {
            string yaml = ConfigYaml.ExpandScalar(Sample, "options.locale", new[] { "web", "telegram", "ai" });
            yaml = ConfigYaml.Apply(yaml, new Dictionary<string, string> { ["options.locale.web"] = "en" });
            var s = WebUi.ParseSettings(yaml);
            Assert.Equal("en", s.options.locale.web);
            Assert.Equal("ru", s.options.locale.telegram);
            Assert.Equal("ru", s.options.locale.ai);
            Assert.Equal(180, s.options.timeoffset);
        }
    }
}
