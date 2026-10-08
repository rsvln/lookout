using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Lookout
{
    // Secrets in the config YAML (passwords, tokens, API keys) are shown as ******** in the web UI and put back
    // from the file on disk when the editor still has the mask. Values are replaced by source span so comments stay.
    public static class ConfigYaml
    {
        public const string Mask = "********";

        static readonly HashSet<string> SecretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "password", "token", "apikey", "secret" };

        public static bool IsSecretKey(string key) => key != null && SecretKeys.Contains(key);

        public static bool IsSecretPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            int dot = path.LastIndexOf('.');
            string last = dot < 0 ? path : path.Substring(dot + 1);
            int bracket = last.IndexOf('[');
            if (bracket >= 0) last = last.Substring(0, bracket);
            return IsSecretKey(last);
        }

        public static string MaskSecrets(string yaml)
        {
            if (string.IsNullOrEmpty(yaml)) return yaml;
            try
            {
                var spans = Collect(yaml).Where(s => IsSecretKey(LastKey(s.path)) && s.value != Mask && s.value != "").OrderByDescending(s => s.start).ToList();
                var sb = new StringBuilder(yaml);
                foreach (var s in spans)
                    sb.Remove(s.start, s.end - s.start).Insert(s.start, FormatScalar(Mask));
                return sb.ToString();
            }
            catch (YamlDotNet.Core.YamlException) { return yaml; }
        }

        // Submitted text from the editor: every secret that is still the mask is taken from `original` (the file).
        public static string RestoreSecrets(string submitted, string original)
        {
            if (string.IsNullOrEmpty(submitted) || string.IsNullOrEmpty(original)) return submitted;
            Dictionary<string, string> fromFile;
            List<Span> inSubmit;
            try
            {
                fromFile = Collect(original).Where(s => IsSecretKey(LastKey(s.path))).GroupBy(s => s.path).ToDictionary(g => g.Key, g => g.Last().value);
                inSubmit = Collect(submitted).Where(s => IsSecretKey(LastKey(s.path)) && s.value == Mask).OrderByDescending(s => s.start).ToList();
            }
            catch (YamlDotNet.Core.YamlException) { return submitted; }

            var sb = new StringBuilder(submitted);
            foreach (var s in inSubmit)
            {
                if (!fromFile.TryGetValue(s.path, out string real) || real == Mask) continue;
                sb.Remove(s.start, s.end - s.start).Insert(s.start, FormatScalar(real));
            }
            return sb.ToString();
        }

        // Replace the scalar (or sequence, for a list of scalars) at `path`; if the last key is missing, it is added
        // under its parent. Path: "mqtt.password", "frigate.cameras[camera=front].cooldown", "telegram.chatids".
        public static string Set(string yaml, string path, string value)
        {
            if (yaml == null) yaml = "";
            var parts = ParsePath(path);
            if (parts.Count == 0) return yaml;
            try
            {
                var root = Root(yaml);
                if (root == null) return yaml;
                if (TryReplace(yaml, root, parts, 0, value, out string next)) return next;
                return TryInsert(yaml, root, parts, 0, value) ?? yaml;
            }
            catch (YamlDotNet.Core.YamlException) { return yaml; }
        }

        public static string Apply(string yaml, IDictionary<string, string> fields)
        {
            if (fields == null) return yaml;
            foreach (var kv in fields)
            {
                if (kv.Value == null) continue;
                if (IsSecretPath(kv.Key) && kv.Value == Mask) continue;
                yaml = Set(yaml, kv.Key, kv.Value);
            }
            return yaml;
        }

        public static string FormatScalar(string v)
        {
            v ??= "";
            if (v == "") return "\"\"";
            if (v is "true" or "false" or "null") return v;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !v.Contains(',') && !v.Contains(' '))
                return v;
            if (Regex.IsMatch(v, @"^[A-Za-z0-9_./+-]+$")) return v;
            return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // ---- walking the tree ---------------------------------------------------------------------------------------

        record Span(string path, int start, int end, string value);

        static YamlNode Root(string yaml)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
        }

        static List<Span> Collect(string yaml)
        {
            var found = new List<Span>();
            var root = Root(yaml);
            if (root != null) Walk(root, "", found);
            return found;
        }

        static void Walk(YamlNode node, string path, List<Span> found)
        {
            if (node is YamlMappingNode map)
            {
                foreach (var kv in map.Children)
                {
                    string key = kv.Key is YamlScalarNode ks ? ks.Value : "";
                    string child = path == "" ? key : path + "." + key;
                    Walk(kv.Value, child, found);
                }
            }
            else if (node is YamlSequenceNode seq)
            {
                int i = 0;
                foreach (var item in seq.Children)
                    Walk(item, path + "." + i++, found);
            }
            else if (node is YamlScalarNode scalar)
            {
                int start = (int)scalar.Start.Index, end = (int)scalar.End.Index;
                if (end > start) found.Add(new Span(path, start, end, scalar.Value ?? ""));
            }
        }

        static string LastKey(string path)
        {
            int dot = path.LastIndexOf('.');
            return dot < 0 ? path : path.Substring(dot + 1);
        }

        public static List<(string key, string selector)> ParsePath(string path)
        {
            var parts = new List<(string, string)>();
            foreach (Match m in Regex.Matches(path ?? "", @"([^.\[\]]+)(?:\[([^\]]+)\])?"))
                parts.Add((m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null));
            return parts;
        }

        static bool SpanOf(YamlNode node, string yaml, out int start, out int end)
        {
            start = (int)node.Start.Index;
            end = (int)node.End.Index;
            if (node is YamlSequenceNode seq && seq.Children.Count > 0)
            {
                start = seq.Children.Min(c => (int)c.Start.Index);
                end = Math.Max(end, seq.Children.Max(c => (int)c.End.Index));
                while (start > 0 && yaml[start - 1] is ' ' or '\t') start--;
                if (start > 0 && yaml[start - 1] == '-') start--;
            }
            return end > start && start >= 0 && end <= yaml.Length;
        }

        static bool TryReplace(string yaml, YamlNode node, List<(string key, string selector)> parts, int i, string value, out string next)
        {
            next = yaml;
            var child = Navigate(node, parts, i, out YamlMappingNode parent, out string lastKey);
            if (child == null) return false;
            if (!SpanOf(child, yaml, out int start, out int end)) return false;
            string text = child is YamlSequenceNode
                ? FormatSequence(value)
                : FormatScalar(value);
            next = yaml.Substring(0, start) + text + yaml.Substring(end);
            return true;
        }

        static string TryInsert(string yaml, YamlNode node, List<(string key, string selector)> parts, int i, string value)
        {
            var parentParts = parts.Take(parts.Count - 1).ToList();
            YamlNode parentNode = parentParts.Count == 0 ? node : Navigate(node, parentParts, 0, out _, out _);
            if (parentNode is not YamlMappingNode map) return null;
            string key = parts[parts.Count - 1].key;
            if (map.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == key)) return null;
            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            int insertAt, indent;
            if (map.Children.Count > 0)
            {
                var last = map.Children.Last();
                var lastNode = last.Value.End.Index >= last.Key.End.Index ? last.Value : last.Key;
                insertAt = (int)lastNode.End.Index;
                indent = (int)last.Key.Start.Column - 1;
            }
            else
            {
                insertAt = (int)map.End.Index;
                indent = Math.Max(0, (int)map.Start.Column + 1);
            }
            if (insertAt < 0 || insertAt > yaml.Length) return null;
            string line = nl + new string(' ', Math.Max(0, indent)) + key + ": " + FormatScalar(value);
            return yaml.Substring(0, insertAt) + line + yaml.Substring(insertAt);
        }

        static YamlNode Navigate(YamlNode node, List<(string key, string selector)> parts, int i, out YamlMappingNode parent, out string lastKey)
        {
            parent = null;
            lastKey = null;
            for (; i < parts.Count; i++)
            {
                var (key, sel) = parts[i];
                if (node is not YamlMappingNode map) return null;
                parent = map;
                lastKey = key;
                YamlNode child = null;
                foreach (var kv in map.Children)
                    if (kv.Key is YamlScalarNode ks && ks.Value == key) { child = kv.Value; break; }
                if (child == null) return null;
                if (sel != null)
                {
                    if (child is not YamlSequenceNode seq) return null;
                    child = Pick(seq, sel);
                    if (child == null) return null;
                }
                if (i == parts.Count - 1) return child;
                node = child;
            }
            return node;
        }

        static YamlNode Pick(YamlSequenceNode seq, string sel)
        {
            if (int.TryParse(sel, out int idx))
                return idx >= 0 && idx < seq.Children.Count ? seq.Children[idx] : null;
            int eq = sel.IndexOf('=');
            if (eq <= 0) return null;
            string field = sel.Substring(0, eq), want = sel.Substring(eq + 1);
            foreach (var item in seq.Children.OfType<YamlMappingNode>())
                foreach (var kv in item.Children)
                    if (kv.Key is YamlScalarNode ks && ks.Value == field && kv.Value is YamlScalarNode vs && vs.Value == want)
                        return item;
            return null;
        }

        static string FormatSequence(string csv)
        {
            var items = (csv ?? "").Split(',').Select(s => s.Trim()).Where(s => s != "");
            return "[" + string.Join(", ", items.Select(FormatScalar)) + "]";
        }
    }

    // The structured settings form: a list of fields the UI can render, applied back onto the YAML by path.
    public static class SettingsForm
    {
        public record Field(string path, string type, string value, string label, string[] options = null);
        public record Group(string id, string title, List<Field> fields);
        public record CameraGroup(string camera, List<Field> fields);

        public static object Snapshot(SettingsFile s)
        {
            var f = s?.frigate; var m = s?.mqtt; var tg = s?.telegram; var w = s?.web; var o = s?.options; var ai = s?.ai; var fr = s?.fr;
            var groups = new List<Group>
            {
                G("frigate", F("frigate.host", "text", f?.host), F("frigate.port", "number", n(f?.port)),
                    F("frigate.clipspath", "text", f?.clipspath), F("frigate.dbpath", "text", f?.dbpath),
                    F("frigate.recordingspath", "text", f?.recordingspath), F("frigate.recordingsoriginalpath", "text", f?.recordingsoriginalpath)),
                G("mqtt", F("mqtt.host", "text", m?.host), F("mqtt.port", "number", n(m?.port)),
                    F("mqtt.user", "text", m?.user), F("mqtt.password", "password", Mask(m?.password)),
                    F("mqtt.eventstopic", "text", m?.eventstopic), F("mqtt.reviewstopic", "text", m?.reviewstopic)),
                G("telegram", F("telegram.token", "password", Mask(tg?.token)),
                    F("telegram.chatids", "text", tg?.chatids == null ? "" : string.Join(", ", tg.chatids)),
                    F("telegram.clipsizecheck", "number", n(tg?.clipsizecheck)), F("telegram.clipsizesplit", "number", n(tg?.clipsizesplit)),
                    F("telegram.mediagrouplimit", "number", n(tg?.mediagrouplimit))),
                G("web", F("web.user", "text", w?.user), F("web.password", "password", Mask(w?.password)),
                    F("web.publicurl", "text", w?.publicurl), F("web.auth", "select", w?.auth ?? "basic", "basic", "form"),
                    F("web.sessionhours", "number", n(w?.sessionhours ?? 168)), F("web.secret", "password", Mask(w?.secret))),
                G("options", F("options.timeoffset", "number", n(o?.timeoffset)), F("options.timeout", "number", n(o?.timeout)),
                    F("options.retry", "number", n(o?.retry)), F("options.retrymax", "number", n(o?.retrymax)),
                    F("options.retrybackoff", "number", n(o?.retrybackoff)), F("options.gifwidth", "number", n(o?.gifwidth)),
                    F("options.buttons", "checkbox", b(o?.buttons ?? false)),
                    F("options.quiet.from", "text", o?.quiet?.from), F("options.quiet.to", "text", o?.quiet?.to),
                    F("options.quiet.mode", "select", o?.quiet?.mode ?? "silent", "silent", "snapshot", "none")),
                G("ai", F("ai.url", "text", ai?.url), F("ai.model", "text", ai?.model),
                    F("ai.humanprompt", "textarea", ai?.humanprompt), F("ai.nonhumanprompt", "textarea", ai?.nonhumanprompt),
                    F("ai.numpredict", "number", n(ai?.numpredict)), F("ai.temperature", "text", ai == null ? "" : ai.temperature.ToString(CultureInfo.InvariantCulture)),
                    F("ai.thinking", "checkbox", b(ai?.thinking ?? false)), F("ai.resizetowidth", "number", n(ai?.resizetowidth))),
                G("fr", F("fr.url", "text", fr?.url), F("fr.apikey", "password", Mask(fr?.apikey)),
                    F("fr.confidence", "text", fr == null ? "" : fr.confidence.ToString(CultureInfo.InvariantCulture)),
                    F("fr.detprobthreshold", "text", fr == null ? "" : fr.detprobthreshold.ToString(CultureInfo.InvariantCulture))),
            };
            var cameras = (f?.cameras ?? new List<Camera>()).Select(c => new CameraGroup(c.camera, new List<Field>
            {
                F($"frigate.cameras[camera={c.camera}].snapshot", "checkbox", b(c.snapshot)),
                F($"frigate.cameras[camera={c.camera}].clip", "checkbox", b(c.clip)),
                F($"frigate.cameras[camera={c.camera}].gif", "checkbox", b(c.gif)),
                F($"frigate.cameras[camera={c.camera}].ai", "checkbox", b(c.ai)),
                F($"frigate.cameras[camera={c.camera}].fr", "checkbox", b(c.fr)),
                F($"frigate.cameras[camera={c.camera}].trueend", "checkbox", b(c.trueend)),
                F($"frigate.cameras[camera={c.camera}].topic", "select", c.topic ?? "reviews", "reviews", "events"),
                F($"frigate.cameras[camera={c.camera}].snapshottrigger", "select", c.snapshottrigger ?? "end", "end", "new"),
                F($"frigate.cameras[camera={c.camera}].cooldown", "number", n(c.cooldown)),
                F($"frigate.cameras[camera={c.camera}].cooldownperobject", "checkbox", b(c.cooldownperobject)),
            })).ToList();
            return new { groups, cameras };
        }

        static string Mask(string v) => string.IsNullOrEmpty(v) ? "" : ConfigYaml.Mask;
        static string n(long? v) => v == null || v == 0 ? "0" : v.Value.ToString(CultureInfo.InvariantCulture);
        static string n(int? v) => v == null ? "" : v.Value.ToString(CultureInfo.InvariantCulture);
        static string b(bool v) => v ? "true" : "false";
        static string L(string path)
        {
            string key = "web.field." + path.Replace("frigate.cameras[camera=", "camera.").Replace("]", "");
            int cut = key.IndexOf("camera.");
            if (cut >= 0) key = "web.field." + key.Substring(key.LastIndexOf('.') + 1);
            else
            {
                int dot = path.LastIndexOf('.');
                key = "web.field." + (dot < 0 ? path : path.Substring(dot + 1));
            }
            return L10n.Web.Has(key) ? L10n.Web.T(key) : key.Substring("web.field.".Length);
        }
        static Field F(string path, string type, string value, params string[] options) =>
            new Field(path, type, value ?? "", L(path), options is { Length: > 0 } ? options : null);
        static Group G(string id, params Field[] fields) => new Group(id, L10n.Web.Has("web.config.group." + id) ? L10n.Web.T("web.config.group." + id) : id, fields.ToList());
    }
}
