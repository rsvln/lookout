using System.Net;
using System.Text.RegularExpressions;

namespace Lookout
{
    // Telegram captions are sent as HTML: the plain caption (the one kept in the local DB and sent to other notifiers)
    // is escaped, and with telegram.links on, the review / event ids in it become links to Frigate or Lookout.
    internal static class TelegramLinks
    {
        const int CaptionLimit = 1024;
        static int lookoutUrlWarned;

        // Root address of the link target, or null when links are off (or a Lookout target has no address).
        internal static string BaseUrl()
        {
            var links = Program.settings?.telegram?.links;
            if (links == null || !links.enabled)
                return null;
            bool lookout = string.Equals(links.target, "lookout", StringComparison.OrdinalIgnoreCase);
            string url = links.url?.Trim();
            if (string.IsNullOrEmpty(url) && lookout)
                url = Program.settings.web?.publicurl?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                if (lookout)
                {
                    if (Interlocked.Exchange(ref lookoutUrlWarned, 1) == 0)
                        Program.Log("app", "", "", "telegram.links: target lookout needs telegram.links.url or web.publicurl (Lookout's address), links are off");
                    return null;
                }
                var f = Program.settings.frigate;
                url = "http://" + f.host + ":" + f.port;
            }
            return url.TrimEnd('/');
        }

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
