using System.Net.Http.Headers;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Lookout
{
    public class NtfyNotifier : INotifier
    {
        readonly NotifierSettings s;
        readonly HttpClient http;
        public string Name => "ntfy:" + s.url;

        public NtfyNotifier(NotifierSettings s, HttpClient http) { this.s = s; this.http = http; }

        public async Task SendAsync(NotifyMessage msg, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, s.url);
            req.Headers.TryAddWithoutValidation("Title", NotifierHub.TitleOf(s, msg));
            req.Headers.TryAddWithoutValidation("Priority", msg.Silent ? "2" : "4");
            req.Headers.TryAddWithoutValidation("Tags", msg.Camera ?? "lookout");
            if (!string.IsNullOrEmpty(s.token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + s.token);
            string snap = s.attach ? NotifierHub.FirstSnapshot(msg) : null;
            if (snap != null)
            {
                req.Headers.TryAddWithoutValidation("Filename", Path.GetFileName(snap));
                req.Headers.TryAddWithoutValidation("Message", msg.Body ?? "");
                req.Content = new ByteArrayContent(await File.ReadAllBytesAsync(snap, ct));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            }
            else
                req.Content = new StringContent(msg.Body ?? "", Encoding.UTF8, "text/plain");
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
        }
    }

    public class DiscordNotifier : INotifier
    {
        readonly NotifierSettings s;
        readonly HttpClient http;
        public string Name => "discord:" + s.url;

        public DiscordNotifier(NotifierSettings s, HttpClient http) { this.s = s; this.http = http; }

        public async Task SendAsync(NotifyMessage msg, CancellationToken ct = default)
        {
            string text = (msg.Body ?? "").Length > 2000 ? (msg.Body ?? "").Substring(0, 1997) + "..." : (msg.Body ?? "");
            string snap = s.attach ? NotifierHub.FirstSnapshot(msg) : null;
            HttpContent content;
            if (snap != null)
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(new { content = text, username = "Lookout" }), Encoding.UTF8, "application/json"), "payload_json");
                var file = new ByteArrayContent(await File.ReadAllBytesAsync(snap, ct));
                file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(file, "files[0]", Path.GetFileName(snap));
                content = form;
            }
            else
                content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { content = text, username = "Lookout" }), Encoding.UTF8, "application/json");
            using var res = await http.PostAsync(s.url, content, ct);
            res.EnsureSuccessStatusCode();
        }
    }

    public class MatrixNotifier : INotifier
    {
        readonly NotifierSettings s;
        readonly HttpClient http;
        public string Name => "matrix:" + s.room;

        public MatrixNotifier(NotifierSettings s, HttpClient http) { this.s = s; this.http = http; }

        public async Task SendAsync(NotifyMessage msg, CancellationToken ct = default)
        {
            string hs = s.homeserver.TrimEnd('/');
            string snap = s.attach ? NotifierHub.FirstSnapshot(msg) : null;
            object body;
            if (snap != null)
            {
                using var up = new HttpRequestMessage(HttpMethod.Post, hs + "/_matrix/media/v3/upload?filename=" + Uri.EscapeDataString(Path.GetFileName(snap)));
                up.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.token);
                up.Content = new ByteArrayContent(await File.ReadAllBytesAsync(snap, ct));
                up.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                using var upRes = await http.SendAsync(up, ct);
                upRes.EnsureSuccessStatusCode();
                using var doc = System.Text.Json.JsonDocument.Parse(await upRes.Content.ReadAsStringAsync(ct));
                string uri = doc.RootElement.GetProperty("content_uri").GetString();
                body = new { msgtype = "m.image", body = Path.GetFileName(snap), url = uri, info = new { mimetype = "image/jpeg" } };
            }
            else
                body = new { msgtype = "m.text", body = msg.Body ?? "" };

            string txn = Guid.NewGuid().ToString("N");
            using var send = new HttpRequestMessage(HttpMethod.Put,
                hs + "/_matrix/client/v3/rooms/" + Uri.EscapeDataString(s.room) + "/send/m.room.message/" + txn);
            send.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.token);
            send.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(send, ct);
            res.EnsureSuccessStatusCode();
        }
    }

    public class WebhookNotifier : INotifier
    {
        readonly NotifierSettings s;
        readonly HttpClient http;
        public string Name => "webhook:" + s.url;

        public WebhookNotifier(NotifierSettings s, HttpClient http) { this.s = s; this.http = http; }

        public async Task SendAsync(NotifyMessage msg, CancellationToken ct = default)
        {
            var payload = new { kind = msg.Kind, id = msg.Id, camera = msg.Camera, body = msg.Body, silent = msg.Silent };
            using var res = await http.PostAsync(s.url, new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            res.EnsureSuccessStatusCode();
        }
    }

    // Extra Telegram chats listed under notifiers:; the main `telegram:` block is still sent by the workers.
    public class TelegramNotifier : INotifier
    {
        readonly NotifierSettings s;
        public string Name => "telegram:" + string.Join(",", s.chatids);

        public TelegramNotifier(NotifierSettings s) { this.s = s; }

        public async Task SendAsync(NotifyMessage msg, CancellationToken ct = default)
        {
            var bot = Program.bot;
            if (bot == null) return;
            string snap = s.attach ? NotifierHub.FirstSnapshot(msg) : null;
            foreach (var chat in s.chatids)
            {
                if (snap != null)
                {
                    await using var fs = File.OpenRead(snap);
                    await bot.SendPhoto(chat, InputFile.FromStream(fs), caption: msg.Body, disableNotification: msg.Silent, cancellationToken: ct);
                }
                else
                    await bot.SendMessage(chat, msg.Body ?? "", disableNotification: msg.Silent, cancellationToken: ct);
            }
        }
    }
}
