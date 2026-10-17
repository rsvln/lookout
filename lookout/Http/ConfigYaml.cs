using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Lookout
{
    // YAML edits by path: values are replaced by source span so comments stay.
    public static class ConfigYaml
    {
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
                    // An empty form field for a key the YAML doesn't have means "not set": nothing is added
                    // (an added `numpredict: ""` would not even parse).
                    if (string.IsNullOrEmpty(value)) return yaml;
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
                try { yaml = Set(yaml, kv.Key, kv.Value); }
                catch (Exception ex) { throw new InvalidOperationException(kv.Key + ": " + ex.Message, ex); }
            }
            return yaml;
        }

        // A setting written as one scalar for several keys ("locale: ru") becomes a block with that value under each
        // key ("locale:\n  web: ru\n  telegram: ru\n  ai: ru"), so one of them can then be changed by path.
        public static string ExpandScalar(string yaml, string path, IEnumerable<string> keys)
        {
            if (string.IsNullOrEmpty(yaml)) return yaml;
            try
            {
                var root = Root(yaml);
                if (root == null) return yaml;
                var parts = ParsePath(path);
                if (Navigate(root, parts, 0, out YamlMappingNode parent, out string lastKey) is not YamlScalarNode scalar || parent == null)
                    return yaml;
                var keyNode = parent.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == lastKey);
                if (keyNode == null || !SpanOf(scalar, yaml, out int start, out int end)) return yaml;
                string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
                string indent = new string(' ', (int)keyNode.Start.Column - 1 + 2);
                // The key's colon is followed by a space before the scalar: the block starts on the next line.
                int from = start;
                while (from > 0 && yaml[from - 1] == ' ') from--;
                string block = string.Concat(keys.Select(k => nl + indent + k + ": " + FormatScalar(scalar.Value)));
                return yaml.Substring(0, from) + block + yaml.Substring(end);
            }
            catch (YamlDotNet.Core.YamlException) { return yaml; }
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

        static YamlNode Root(string yaml)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
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
            string text = FormatValue(lastKey, value, child is YamlSequenceNode);
            next = yaml.Substring(0, start) + text + yaml.Substring(end);
            return true;
        }

        static string TryInsert(string yaml, YamlNode node, List<(string key, string selector)> parts, int i, string value)
        {
            // The deepest existing mapping on the path; the missing keys below it (e.g. "links" of telegram.links.url)
            // are added as nested blocks.
            int depth = parts.Count - 1;
            YamlNode parentNode = depth == 0 ? node : Navigate(node, parts.Take(depth).ToList(), 0, out _, out _);
            while (parentNode == null && depth > 0)
            {
                depth--;
                if (parts[depth].selector != null) return null;
                parentNode = depth == 0 ? node : Navigate(node, parts.Take(depth).ToList(), 0, out _, out _);
            }
            if (parentNode is not YamlMappingNode map) return null;
            string key = parts[depth].key;
            if (map.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == key)) return null;
            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            int insertAt, indent;
            if (map.Children.Count > 0)
            {
                var last = map.Children.Last();
                // SpanOf covers a flow sequence up to its closing bracket ("chatids: ['1']"); NodeEnd stops inside it.
                int valueEnd = SpanOf(last.Value, yaml, out _, out int spanEnd) ? Math.Max(spanEnd, NodeEnd(last.Value)) : NodeEnd(last.Value);
                insertAt = Math.Max((int)last.Key.End.Index, valueEnd);
                indent = (int)last.Key.Start.Column - 1;
            }
            else
            {
                insertAt = (int)map.End.Index;
                indent = Math.Max(0, (int)map.Start.Column + 1);
            }
            if (insertAt < 0 || insertAt > yaml.Length) return null;
            string leaf = parts[parts.Count - 1].key;
            string formatted = FormatValue(leaf, value, false);
            var line = new System.Text.StringBuilder();
            for (int d = depth; d < parts.Count; d++)
            {
                line.Append(nl).Append(' ', Math.Max(0, indent + 2 * (d - depth))).Append(parts[d].key).Append(':');
                if (d == parts.Count - 1) line.Append(' ').Append(formatted);
            }
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
                // SpanOf covers a flow sequence up to its closing bracket ("chatids: ['1']"); NodeEnd stops inside it.
                int valueEnd = SpanOf(last.Value, yaml, out _, out int spanEnd) ? Math.Max(spanEnd, NodeEnd(last.Value)) : NodeEnd(last.Value);
                insertAt = Math.Max((int)last.Key.End.Index, valueEnd);
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

        // Keys whose value is a list of scalars ("a, b" in the form) and the per-camera object thresholds.
        static readonly HashSet<string> ListKeys = new(StringComparer.OrdinalIgnoreCase) { "chatids", "severity", "zones" };

        static string FormatValue(string key, string value, bool isSequence)
        {
            if (string.Equals(key, "objects", StringComparison.OrdinalIgnoreCase)) return FormatObjects(value);
            if (isSequence || (key != null && ListKeys.Contains(key))) return FormatSequence(value);
            return FormatScalar(value);
        }

        // "person:60, car, dog:40" -> [{label: person, percent: 60}, {label: car}, {label: dog, percent: 40}]
        static string FormatObjects(string csv)
        {
            var items = (csv ?? "").Split(',').Select(s => s.Trim()).Where(s => s != "").Select(item =>
            {
                int colon = item.LastIndexOf(':');
                string label = colon > 0 ? item.Substring(0, colon).Trim() : item;
                string percent = colon > 0 ? item.Substring(colon + 1).Trim() : "";
                return int.TryParse(percent, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)
                    ? "{label: " + FormatScalar(label) + ", percent: " + p + "}"
                    : "{label: " + FormatScalar(label) + "}";
            });
            return "[" + string.Join(", ", items) + "]";
        }

        static string FormatSequence(string csv)
        {
            var items = (csv ?? "").Split(',').Select(s => s.Trim()).Where(s => s != "");
            return "[" + string.Join(", ", items.Select(FormatScalar)) + "]";
        }
    }
}
