using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lookout
{
    // The Config form, Delphi-DFM style: lookout/forms/*.json say where each setting goes (tree.json: the tree of
    // nodes; one template per node: sections -> rows -> fields with width, hint, enabledBy), and the settings model
    // (Models.cs) gives each field's value and type. Saving goes through ConfigYaml by path, as before.
    public static class SettingsForm
    {
        // What the page gets: nodes -> sections -> rows -> fields; paths are full YAML paths.
        public record FieldView(string path, string type, string value, string label, string[] options, int width, string hint, string enabledBy);
        public record SectionView(string title, List<List<FieldView>> rows);

        // Template files.
        internal class ItemT
        {
            public string path { get; set; }
            public int? width { get; set; }
            public string control { get; set; }
            public JsonElement options { get; set; }
            public string label { get; set; }
            public string hint { get; set; }
            public string enabledBy { get; set; }
            // Value shown when the setting (or an object on its path) is absent from the config.
            public string @default { get; set; }
        }
        internal class SectionT
        {
            public string title { get; set; }
            public string @base { get; set; }
            public List<List<ItemT>> rows { get; set; } = new();
        }
        internal class TemplateT
        {
            public string @base { get; set; }
            public string title { get; set; }
            public string hint { get; set; }
            public List<SectionT> sections { get; set; } = new();
        }
        internal class TreeNodeT
        {
            public string node { get; set; }
            public JsonElement children { get; set; }
        }
        internal class TreeT
        {
            public List<TreeNodeT> nodes { get; set; } = new();
            // Model properties deliberately left out of the form, with the reason.
            public Dictionary<string, string> hidden { get; set; } = new();
        }

        public static readonly string[] NotifierTypes = { "ntfy", "discord", "matrix", "webhook", "telegram" };

        static string FormsDir => Path.Combine(Program.appLocation ?? AppContext.BaseDirectory, "forms");
        static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        static readonly object loadLock = new();
        static Dictionary<string, TemplateT> templates;
        static TreeT tree;

        // Loads (once) and checks every template: a bad file or a path missing from the model fails with the file name.
        internal static void Load(bool reload = false)
        {
            lock (loadLock)
            {
                if (templates != null && !reload) return;
                var t = new Dictionary<string, TemplateT>(StringComparer.OrdinalIgnoreCase);
                TreeT tr = null;
                foreach (var file in Directory.GetFiles(FormsDir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    try
                    {
                        string json = File.ReadAllText(file);
                        if (name == "tree") tr = JsonSerializer.Deserialize<TreeT>(json, JsonOpts);
                        else t[name] = JsonSerializer.Deserialize<TemplateT>(json, JsonOpts);
                    }
                    catch (Exception ex) { throw new InvalidDataException("forms/" + Path.GetFileName(file) + ": " + ex.Message, ex); }
                }
                if (tr == null) throw new InvalidDataException("forms/tree.json is missing");
                foreach (var (name, tmpl) in t)
                    foreach (var (path, _) in TemplatePaths(tmpl))
                        if (ResolveType(path) == null)
                            throw new InvalidDataException($"forms/{name}.json: '{path}' is not a setting of the model");
                templates = t;
                tree = tr;
            }
        }

        internal static TreeT Tree() { Load(); return tree; }
        internal static IReadOnlyDictionary<string, TemplateT> Templates() { Load(); return templates; }

        // Full paths of a template's fields (placeholders kept), with the item.
        internal static IEnumerable<(string path, ItemT item)> TemplatePaths(TemplateT tmpl) =>
            tmpl.sections.SelectMany(s => s.rows.SelectMany(r => r).Select(i => (Join(s.@base ?? tmpl.@base, i.path), i)));

        static string Join(string b, string p) => string.IsNullOrEmpty(b) ? p : b + "." + p;

        public static object Snapshot(SettingsFile s)
        {
            Load();
            s ??= new SettingsFile();
            var cameras = (s.frigate?.cameras ?? new List<Camera>())
                .Where(c => !string.IsNullOrEmpty(c.camera))
                .Select(c => new { camera = c.camera, sections = Render("camera", s, ("{camera}", c.camera)) }).ToList();
            var notifiers = (s.notifiers ?? new List<NotifierSettings>()).Select((n, i) =>
            {
                string type = NotifierType(n);
                return new { index = i, type, sections = Render("notifier-" + type, s, ("{index}", i.ToString(CultureInfo.InvariantCulture))) };
            }).ToList();
            var notifierTemplates = NotifierTypes.ToDictionary(type => type, type =>
                Render("notifier-" + type, new SettingsFile { notifiers = new List<NotifierSettings> { new NotifierSettings { type = type } } }, ("{index}", "0")));
            return new
            {
                tree = tree.nodes.Select(n => NodeView(n, s)).ToList(),
                cameras,
                cameraTemplate = Render("camera", s, ("{camera}", "{camera}")),
                notifiers,
                notifierTemplates
            };
        }

        static string NotifierType(NotifierSettings n)
        {
            string type = string.IsNullOrWhiteSpace(n?.type) ? "ntfy" : n.type.Trim().ToLowerInvariant();
            return NotifierTypes.Contains(type) ? type : "ntfy";
        }

        static object NodeView(TreeNodeT n, SettingsFile s)
        {
            templates.TryGetValue(n.node, out var tmpl);
            object children = n.children.ValueKind switch
            {
                JsonValueKind.String => n.children.GetString(),
                JsonValueKind.Array => n.children.Deserialize<List<TreeNodeT>>(JsonOpts).Select(c => NodeView(c, s)).ToList(),
                _ => null
            };
            return new
            {
                id = n.node,
                title = T(tmpl?.title ?? "web.config.group." + n.node, n.node),
                hint = tmpl?.hint == null ? null : T(tmpl.hint, ""),
                sections = tmpl == null ? new List<SectionView>() : Render(n.node, s),
                children
            };
        }

        internal static List<SectionView> Render(string name, SettingsFile s, params (string key, string value)[] placeholders)
        {
            Load();
            var tmpl = templates[name];
            string Fill(string p) => placeholders.Aggregate(p ?? "", (acc, ph) => acc.Replace(ph.key, ph.value));
            return tmpl.sections.Select(sec =>
            {
                string b = Fill(sec.@base ?? tmpl.@base);
                var rows = sec.rows.Select(row => row.Select(item => View(s, Join(b, item.path), item, b)).ToList()).ToList();
                return new SectionView(sec.title == null ? null : T(sec.title, sec.title), rows);
            }).ToList();
        }

        static FieldView View(SettingsFile s, string path, ItemT item, string b)
        {
            var (type, value, missing) = Resolve(s, path);
            string control = item.control ?? (item.options.ValueKind != JsonValueKind.Undefined ? "select" : Control(type));
            string text = missing && item.@default != null ? item.@default : Format(type, value);
            string[] options = item.options.ValueKind switch
            {
                JsonValueKind.String when item.options.GetString() == "$locales" => Locales(),
                JsonValueKind.Array => item.options.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(),
                _ => null
            };
            // A select keeps a value it doesn't list (e.g. a locale whose file is gone), so saving doesn't change it.
            if (options != null && !options.Contains(text)) options = options.Append(text).ToArray();
            return new FieldView(path, control, text, item.label != null ? T(item.label, item.label) : L(path), options,
                Math.Clamp(item.width ?? (control == "textarea" ? 4 : 1), 1, 4),
                item.hint == null ? null : T(item.hint, ""),
                item.enabledBy == null ? null : Join(b, item.enabledBy));
        }

        static string[] Locales()
        {
            try { return Directory.GetFiles(L10n.LocalesDir, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToArray(); }
            catch { return new[] { "en" }; }
        }

        // The model's type and current value at a path. Objects missing on the way are taken with the model's defaults,
        // and `missing` tells that the setting is absent from the config.
        internal static (Type type, object value, bool missing) Resolve(SettingsFile s, string path)
        {
            object obj = s;
            Type t = typeof(SettingsFile);
            bool missing = false;
            foreach (var (key, selector) in ConfigYaml.ParsePath(path))
            {
                var prop = t.GetProperty(key, BindingFlags.Public | BindingFlags.Instance)
                           ?? throw new InvalidDataException($"'{path}' is not a setting of the model");
                t = prop.PropertyType;
                obj = obj == null ? null : prop.GetValue(obj);
                if (selector != null)
                {
                    var elem = ListElement(t) ?? throw new InvalidDataException($"'{path}': {key} is not a list");
                    obj = Pick(obj as IList, elem, selector);
                    t = elem;
                }
                if (obj == null && IsObject(t))
                {
                    missing = true;
                    obj = Activator.CreateInstance(t);
                }
            }
            return (t, obj, missing || obj == null);
        }

        // Type at a path (placeholders allowed), or null when the model has no such setting.
        internal static Type ResolveType(string path)
        {
            Type t = typeof(SettingsFile);
            foreach (var (key, selector) in ConfigYaml.ParsePath(path))
            {
                var prop = t.GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null) return null;
                t = prop.PropertyType;
                if (selector != null && (t = ListElement(t)) == null) return null;
            }
            return Control(t) == null ? null : t;
        }

        static object Pick(IList list, Type elem, string selector)
        {
            if (list == null) return null;
            if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                return i >= 0 && i < list.Count ? list[i] : null;
            int eq = selector.IndexOf('=');
            if (eq <= 0) return null;
            var prop = elem.GetProperty(selector.Substring(0, eq));
            string want = selector.Substring(eq + 1);
            foreach (var item in list)
                if (item != null && string.Equals(prop?.GetValue(item)?.ToString(), want, StringComparison.Ordinal))
                    return item;
            return null;
        }

        static Type ListElement(Type t) =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? t.GetGenericArguments()[0] : null;

        static bool IsObject(Type t) => t.IsClass && t != typeof(string) && ListElement(t) == null;

        // Control for a leaf type; null = not a leaf the form can show.
        static string Control(Type t)
        {
            if (t == typeof(bool)) return "checkbox";
            if (t == typeof(int) || t == typeof(long)) return "number";
            if (t == typeof(double) || t == typeof(string)) return "text";
            if (t == typeof(List<string>) || t == typeof(List<Objects>)) return "text";
            return null;
        }

        static string Format(Type t, object v)
        {
            switch (v)
            {
                case null: return "";
                case bool x: return x ? "true" : "false";
                case int x: return x.ToString(CultureInfo.InvariantCulture);
                case long x: return x.ToString(CultureInfo.InvariantCulture);
                case double x: return x.ToString(CultureInfo.InvariantCulture);
                case string x: return x;
                case List<string> x: return string.Join(", ", x);
                case List<Objects> x:
                    return string.Join(", ", x.Where(o => !string.IsNullOrEmpty(o?.label))
                        .Select(o => o.label + ":" + o.percent.ToString(CultureInfo.InvariantCulture)));
                default: return v.ToString();
            }
        }

        // Every leaf setting of the model, lists of objects as "name[]." (for the completeness check).
        internal static IEnumerable<string> ModelLeaves(Type t = null, string prefix = "")
        {
            t ??= typeof(SettingsFile);
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.SetMethod == null || !prop.SetMethod.IsPublic) continue;
                var pt = prop.PropertyType;
                string path = prefix + prop.Name;
                if (Control(pt) != null) yield return path;
                else if (ListElement(pt) is Type elem) { foreach (var x in ModelLeaves(elem, path + "[].")) yield return x; }
                else if (IsObject(pt)) { foreach (var x in ModelLeaves(pt, path + ".")) yield return x; }
            }
        }

        internal static string Normalize(string path) => Regex.Replace(path ?? "", @"\[[^\]]*\]", "[]");

        static string T(string key, string fallback) => L10n.Web.Has(key) ? L10n.Web.T(key) : fallback;

        // Default label of a field: web.field.<full path>, then the notifier / camera / last-part keys.
        static string L(string path)
        {
            if (path != null && path.StartsWith("notifiers[", StringComparison.Ordinal))
            {
                int dot = path.LastIndexOf('.');
                string nk = "web.field.notifier." + (dot < 0 ? path : path.Substring(dot + 1));
                if (L10n.Web.Has(nk)) return L10n.Web.T(nk);
            }
            if (path != null && L10n.Web.Has("web.field." + path)) return L10n.Web.T("web.field." + path);
            string norm = Normalize(path);
            if (L10n.Web.Has("web.field." + norm)) return L10n.Web.T("web.field." + norm);
            int last = path.LastIndexOf('.');
            string key = "web.field." + (last < 0 ? path : path.Substring(last + 1));
            return L10n.Web.Has(key) ? L10n.Web.T(key) : key.Substring("web.field.".Length);
        }
    }
}
