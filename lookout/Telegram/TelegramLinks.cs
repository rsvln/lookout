using System.Net;
using System.Text.RegularExpressions;

namespace Lookout
{
    // Telegram captions are sent as HTML: the plain caption (the one kept in the local DB and sent to other notifiers)
    // is escaped, and with telegram.links on, the review / event ids in it become links to Frigate or Lookout.
    internal static class TelegramLinks
    {
        const int CaptionLimit = 1024;
        static int missingUrlWarned;

        // Root address of the link target, or null when links are off. Both are required: enabled: true and the
        // address of the chosen target (frigateurl or lookouturl); internal addresses are never used instead.
        internal static string BaseUrl()
        {
            var links = Program.settings?.telegram?.links;
            if (links == null || !links.enabled)
                return null;
            string key = ToLookout() ? "lookouturl" : "frigateurl";
            string url = (ToLookout() ? links.lookouturl : links.frigateurl)?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                if (Interlocked.Exchange(ref missingUrlWarned, 1) == 0)
                    Program.Log("app", "", "", "telegram.links is enabled but telegram.links." + key + " is empty, captions are sent without links");
                return null;
            }
            return url.TrimEnd('/');
        }

        // Settings were applied again: warn anew if the address is still missing.
        internal static void Reset() => Interlocked.Exchange(ref missingUrlWarned, 0);

        static bool ToLookout() =>
            string.Equals(Program.settings?.telegram?.links?.target, "lookout", StringComparison.OrdinalIgnoreCase);

        internal static string ReviewUrl(string id)
        {
            string root = BaseUrl();
            if (root == null || string.IsNullOrEmpty(id)) return null;
            return ToLookout() ? root + "/search?q=" + Uri.EscapeDataString(id) : root + "/review?id=" + Uri.EscapeDataString(id);
        }

        internal static string EventUrl(string id)
        {
            string root = BaseUrl();
            if (root == null || string.IsNullOrEmpty(id)) return null;
            return ToLookout() ? root + "/event/" + Uri.EscapeDataString(id) : root + "/explore?event_id=" + Uri.EscapeDataString(id);
        }

        // Caption of a review: the review id and its detections' ids are linked.
        internal static string Review(string plain, string reviewId, IEnumerable<string> eventIds) =>
            Html(plain, new[] { (reviewId, ReviewUrl(reviewId)) }.Concat((eventIds ?? Enumerable.Empty<string>()).Select(id => (id, EventUrl(id)))));

        internal static string Event(string plain, string eventId) => Html(plain, new[] { (eventId, EventUrl(eventId)) });

        // The plain text HTML-escaped; every whole occurrence of an id with a url becomes <a href="url">id</a>.
        internal static string Html(string plain, IEnumerable<(string id, string url)> links)
        {
            string html = WebUtility.HtmlEncode(plain ?? "");
            var map = links.Where(l => !string.IsNullOrEmpty(l.id) && !string.IsNullOrEmpty(l.url))
                           .GroupBy(l => l.id).ToDictionary(g => g.Key, g => g.First().url);
            if (map.Count == 0)
                return html;
            // Ids look like 1792234603.114642-machwt: one pass over the text, longest ids first, whole tokens only.
            string pattern = @"(?<![\w.-])(" + string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?![\w-]|\.\w)";
            return Regex.Replace(html, pattern, m => "<a href=\"" + WebUtility.HtmlEncode(map[m.Value]) + "\">" + m.Value + "</a>");
        }

        // Length of the text Telegram shows for an HTML caption (what its 1024-character limit counts).
        internal static int VisibleLength(string html) =>
            WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", "")).Length;

        // An HTML caption with plain text added below it (AI description, recognized faces). Only the added text is
        // shortened to fit the limit, so the caption's links stay intact.
        internal static string Append(string htmlCaption, string text)
        {
            htmlCaption ??= "";
            text ??= "";
            int room = CaptionLimit - VisibleLength(htmlCaption) - 2;
            if (text.Length > room)
                text = room > 3 ? text.Substring(0, room - 3) + "..." : "";
            return text.Length == 0 ? htmlCaption : htmlCaption + "\n\n" + WebUtility.HtmlEncode(text);
        }
    }
}
