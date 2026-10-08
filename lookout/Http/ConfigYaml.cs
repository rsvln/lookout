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
        public static readonly Regex CameraNameOk = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

        public static string Set(string yaml, string path, string value)
        {
            if (yaml == null) yaml = "";
            var parts = ParsePath(path);
            if (parts.Count == 0) return yaml;
            try
            {
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    var root = Root(yaml);
                    if (root == null) return yaml;
                    if (TryReplace(yaml, root, parts, 0, value, out string next)) return next;
                    if (!TryEnsureSelector(yaml, root, parts, out string ensured) || ensured == yaml)
                        return TryInsert(yaml, root, parts, 0, value) ?? yaml;
                    yaml = ensured;
                }
                return yaml;
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

        // Drops a sequence item, e.g. "frigate.cameras[camera=yard]". The last remaining item becomes [].
        public static string Remove(string yaml, string path)
        {
            if (string.IsNullOrEmpty(yaml) || string.IsNullOrEmpty(path)) return yaml;
            var parts = ParsePath(path);
            if (parts.Count == 0 || parts[parts.Count - 1].selector == null) return yaml;
            try
            {
                var root = Root(yaml);
                if (root == null) return yaml;
                var last = parts[parts.Count - 1];
                var seqParts = parts.Select((p, i) => i == parts.Count - 1 ? (p.key, (string)null) : p).ToList();
                if (Navigate(root, seqParts, 0, out _, out _) is not YamlSequenceNode seq) return yaml;
                var item = Pick(seq, last.selector);
                if (item == null) return yaml;
                if (seq.Children.Count == 1)
                {
                    int from = (int)item.Start.Index;
                    while (from > 0 && yaml[from - 1] is ' ' or '\t' or '\n' or '\r' or '-') from--;
                    if (from > 0 && yaml[from - 1] == ':')
                    {
                        int to = NodeEnd(item);
                        if (!SpanOf(seq, yaml, out _, out int seqEnd)) seqEnd = to;
                        to = Math.Max(to, seqEnd);
                        return yaml.Substring(0, from) + " []" + yaml.Substring(to);
                    }
                }
                if (!SpanOfListItem(yaml, item, out int start, out int end)) return yaml;
                return yaml.Substring(0, start) + yaml.Substring(end);
            }
            catch (YamlDotNet.Core.YamlException) { return yaml; }
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

        static int NodeEnd(YamlNode node)
        {
            int end = (int)node.End.Index;
            if (node is YamlMappingNode map && map.Children.Count > 0)
                end = Math.Max(end, map.Children.Max(kv => Math.Max(NodeEnd(kv.Key), NodeEnd(kv.Value))));
            if (node is YamlSequenceNode seq && seq.Children.Count > 0)
                end = Math.Max(end, seq.Children.Max(NodeEnd));
            return end;
        }

        static bool SpanOf(YamlNode node, string yaml, out int start, out int end)
        {
            start = (int)node.Start.Index;
            end = NodeEnd(node);
            if (node is YamlSequenceNode seq && seq.Children.Count > 0)
            {
                int nodeStart = (int)seq.Start.Index;
                if (nodeStart >= 0 && nodeStart < yaml.Length && yaml[nodeStart] == '[')
                {
                    start = nodeStart;
                    int depth = 0;
                    for (int i = start; i < yaml.Length; i++)
                    {
                        if (yaml[i] == '[') depth++;
                        else if (yaml[i] == ']')
                        {
                            depth--;
                            if (depth == 0) { end = i + 1; break; }
                        }
                    }
                }
                else
                {
                    start = seq.Children.Min(c => (int)c.Start.Index);
                    while (start > 0 && yaml[start - 1] is ' ' or '\t') start--;
                    if (start > 0 && yaml[start - 1] == '-') start--;
                }
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
                insertAt = Math.Max((int)last.Key.End.Index, NodeEnd(last.Value));
                indent = (int)last.Key.Start.Column - 1;
            }
            else
            {
                insertAt = (int)map.End.Index;
                indent = Math.Max(0, (int)map.Start.Column + 1);
            }
            if (insertAt < 0 || insertAt > yaml.Length) return null;
            string formatted = string.Equals(key, "chatids", StringComparison.OrdinalIgnoreCase)
                ? FormatSequence(value)
                : FormatScalar(value);
            string line = nl + new string(' ', Math.Max(0, indent)) + key + ": " + formatted;
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

        static YamlNode Child(YamlMappingNode map, string key)
        {
            foreach (var kv in map.Children)
                if (kv.Key is YamlScalarNode ks && ks.Value == key) return kv.Value;
            return null;
        }

        static bool TryEnsureSelector(string yaml, YamlNode root, List<(string key, string selector)> parts, out string next)
        {
            next = yaml;
            YamlNode node = root;
            for (int i = 0; i < parts.Count; i++)
            {
                var (key, sel) = parts[i];
                if (node is not YamlMappingNode map) return false;
                var child = Child(map, key);
                if (sel == null)
                {
                    if (child == null) return false;
                    node = child;
                    continue;
                }
                if (int.TryParse(sel, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) && idx >= 0 && idx < 64)
                {
                    if (child == null)
                    {
                        next = InsertSequenceWithItem(yaml, map, key, "type", "");
                        return next != yaml;
                    }
                    if (child is not YamlSequenceNode intSeq) return false;
                    if (idx < intSeq.Children.Count) { node = intSeq.Children[idx]; continue; }
                    next = AppendSequenceItem(yaml, intSeq, "type", "");
                    return next != yaml;
                }
                int eq = sel.IndexOf('=');
                if (eq <= 0) return false;
                string field = sel.Substring(0, eq), want = sel.Substring(eq + 1);
                if (child == null)
                {
                    next = InsertSequenceWithItem(yaml, map, key, field, want);
                    return next != yaml;
                }
                if (child is not YamlSequenceNode seq) return false;
                if (Pick(seq, sel) != null) { node = Pick(seq, sel); continue; }
                next = AppendSequenceItem(yaml, seq, field, want);
                return next != yaml;
            }
            return false;
        }

        static string InsertSequenceWithItem(string yaml, YamlMappingNode map, string key, string field, string want)
        {
            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            int insertAt, indent;
            if (map.Children.Count > 0)
            {
                var last = map.Children.Last();
                insertAt = Math.Max((int)last.Key.End.Index, NodeEnd(last.Value));
                indent = (int)last.Key.Start.Column - 1;
            }
            else
            {
                insertAt = (int)map.End.Index;
                indent = Math.Max(0, (int)map.Start.Column + 1);
            }
            if (insertAt < 0 || insertAt > yaml.Length) return yaml;
            string pad = new string(' ', Math.Max(0, indent));
            string itemPad = new string(' ', Math.Max(0, indent + 2));
            string block = nl + pad + key + ":" + nl + itemPad + "- " + field + ": " + FormatScalar(want);
            return yaml.Substring(0, insertAt) + block + yaml.Substring(insertAt);
        }

        static string AppendSequenceItem(string yaml, YamlSequenceNode seq, string field, string want)
        {
            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            string line = "- " + field + ": " + FormatScalar(want);
            if (seq.Children.Count == 0)
            {
                if (!SpanOf(seq, yaml, out int start, out int end) || end < start) return yaml;
                int i = start;
                while (i > 0 && yaml[i - 1] != '\n' && yaml[i - 1] != '\r') i--;
                int lineIndent = 0;
                while (i + lineIndent < yaml.Length && yaml[i + lineIndent] is ' ' or '\t') lineIndent++;
                return yaml.Substring(0, start) + nl + new string(' ', lineIndent + 2) + line + yaml.Substring(end);
            }
            var last = seq.Children.Last();
            int insertAt = NodeEnd(last);
            if (insertAt < 0 || insertAt > yaml.Length) return yaml;
            return yaml.Substring(0, insertAt) + nl + new string(' ', ListItemIndent(yaml, last)) + line + yaml.Substring(insertAt);
        }

        static int ListItemIndent(string yaml, YamlNode item)
        {
            int i = (int)item.Start.Index;
            while (i > 0 && yaml[i - 1] is ' ' or '\t') i--;
            if (i > 0 && yaml[i - 1] == '-') i--;
            int line = i;
            while (line > 0 && yaml[line - 1] != '\n' && yaml[line - 1] != '\r') line--;
            return Math.Max(0, i - line);
        }

        static bool SpanOfListItem(string yaml, YamlNode item, out int start, out int end)
        {
            start = (int)item.Start.Index;
            end = NodeEnd(item);
            while (start > 0 && yaml[start - 1] is ' ' or '\t') start--;
            if (start > 0 && yaml[start - 1] == '-') start--;
            while (start > 0 && yaml[start - 1] is ' ' or '\t') start--;
            int lineStart = start;
            while (lineStart > 0 && yaml[lineStart - 1] != '\n' && yaml[lineStart - 1] != '\r') lineStart--;
            start = lineStart;
            if (end < yaml.Length && yaml[end] == '\r') end++;
            if (end < yaml.Length && yaml[end] == '\n') end++;
            return end > start && start >= 0 && end <= yaml.Length;
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
                G("notifiers"),
                G("web", F("web.user", "text", w?.user), F("web.password", "password", Mask(w?.password)),
                    F("web.publicurl", "text", w?.publicurl), F("web.auth", "select", w?.auth ?? "basic", "basic", "form"),
                    F("web.sessionhours", "number", n(w?.sessionhours ?? 168)), F("web.secret", "password", Mask(w?.secret))),
                G("options", F("options.timeoffset", "number", n(o?.timeoffset)), F("options.timeout", "number", n(o?.timeout)),
                    F("options.retry", "number", n(o?.retry)), F("options.retrymax", "number", n(o?.retrymax)),
                    F("options.retrybackoff", "number", n(o?.retrybackoff)), F("options.correlate", "number", n(o?.correlate)),
                    F("options.gifwidth", "number", n(o?.gifwidth)),
                    F("options.buttons", "checkbox", b(o?.buttons ?? false)),
                    F("options.quiet.from", "text", o?.quiet?.from), F("options.quiet.to", "text", o?.quiet?.to),
                    F("options.quiet.mode", "select", o?.quiet?.mode ?? "silent", "silent", "snapshot", "none")),
                G("ai", F("ai.provider", "select", string.IsNullOrEmpty(ai?.provider) ? "ollama" : ai.provider, "ollama", "openai", "gemini"),
                    F("ai.url", "text", ai?.url), F("ai.model", "text", ai?.model), F("ai.apikey", "password", Mask(ai?.apikey)),
                    F("ai.humanprompt", "textarea", ai?.humanprompt), F("ai.nonhumanprompt", "textarea", ai?.nonhumanprompt),
                    F("ai.numpredict", "number", n(ai?.numpredict)), F("ai.temperature", "text", ai == null ? "" : ai.temperature.ToString(CultureInfo.InvariantCulture)),
                    F("ai.thinking", "checkbox", b(ai?.thinking ?? false)), F("ai.resizetowidth", "number", n(ai?.resizetowidth))),
                G("fr", F("fr.url", "text", fr?.url), F("fr.apikey", "password", Mask(fr?.apikey)),
                    F("fr.confidence", "text", fr == null ? "" : fr.confidence.ToString(CultureInfo.InvariantCulture)),
                    F("fr.detprobthreshold", "text", fr == null ? "" : fr.detprobthreshold.ToString(CultureInfo.InvariantCulture))),
            };
            var cameras = (f?.cameras ?? new List<Camera>()).Select(c => new CameraGroup(c.camera, CameraFields(c.camera, c))).ToList();
            var notifierList = s?.notifiers ?? new List<NotifierSettings>();
            var notifiers = notifierList.Select((n, i) => new
            {
                index = i,
                type = string.IsNullOrWhiteSpace(n.type) ? "ntfy" : n.type.Trim().ToLowerInvariant(),
                fields = NotifierFields(i, n)
            }).ToList();
            var notifierTemplates = new[] { "ntfy", "discord", "matrix", "webhook", "telegram" }
                .ToDictionary(t => t, t => NotifierFields(0, new NotifierSettings { type = t }));
            return new { groups, cameras, cameraTemplate = CameraFields("{camera}"), notifiers, notifierTemplates };
        }

        public static List<Field> NotifierFields(int index, NotifierSettings s)
        {
            s ??= new NotifierSettings();
            string kind = string.IsNullOrWhiteSpace(s.type) ? "ntfy" : s.type.Trim().ToLowerInvariant();
            string p = $"notifiers[{index}]";
            var list = new List<Field> { F($"{p}.type", "select", kind, "ntfy", "discord", "matrix", "webhook", "telegram") };
            switch (kind)
            {
                case "discord":
                case "webhook":
                    list.Add(F($"{p}.url", "text", s.url));
                    break;
                case "matrix":
                    list.Add(F($"{p}.homeserver", "text", s.homeserver));
                    list.Add(F($"{p}.token", "password", Mask(s.token)));
                    list.Add(F($"{p}.room", "text", s.room));
                    break;
                case "telegram":
                    list.Add(F($"{p}.chatids", "text", s.chatids == null ? "" : string.Join(", ", s.chatids)));
                    break;
                default:
                    list.Add(F($"{p}.url", "text", s.url));
                    list.Add(F($"{p}.token", "password", Mask(s.token)));
                    list.Add(F($"{p}.title", "text", s.title));
                    list.Add(F($"{p}.attach", "checkbox", b(s.attach)));
                    break;
            }
            return list;
        }

        public static List<Field> CameraFields(string name, Camera c = null)
        {
            c ??= new Camera { camera = name };
            string p = $"frigate.cameras[camera={name}]";
            return new List<Field>
            {
                F($"{p}.snapshot", "checkbox", b(c.snapshot)),
                F($"{p}.clip", "checkbox", b(c.clip)),
                F($"{p}.gif", "checkbox", b(c.gif)),
                F($"{p}.ai", "checkbox", b(c.ai)),
                F($"{p}.fr", "checkbox", b(c.fr)),
                F($"{p}.trueend", "checkbox", b(c.trueend)),
                F($"{p}.topic", "select", c.topic ?? "reviews", "reviews", "events"),
                F($"{p}.snapshottrigger", "select", c.snapshottrigger ?? "end", "end", "new"),
                F($"{p}.cooldown", "number", n(c.cooldown)),
                F($"{p}.cooldownperobject", "checkbox", b(c.cooldownperobject)),
            };
        }

        static string Mask(string v) => string.IsNullOrEmpty(v) ? "" : ConfigYaml.Mask;
        static string n(long? v) => v == null || v == 0 ? "0" : v.Value.ToString(CultureInfo.InvariantCulture);
        static string n(int? v) => v == null ? "" : v.Value.ToString(CultureInfo.InvariantCulture);
        static string b(bool v) => v ? "true" : "false";
        static string L(string path)
        {
            if (path != null && path.StartsWith("notifiers[", StringComparison.Ordinal))
            {
                int dot = path.LastIndexOf('.');
                string nk = "web.field.notifier." + (dot < 0 ? path : path.Substring(dot + 1));
                if (L10n.Web.Has(nk)) return L10n.Web.T(nk);
            }
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
