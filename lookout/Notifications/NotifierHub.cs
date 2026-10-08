using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Lookout
{
    public class NotifyMessage
    {
        public string Kind { get; set; }
        public string Id { get; set; }
        public string Camera { get; set; }
        public string Body { get; set; }
        public List<string> SnapshotPaths { get; set; } = new List<string>();
        public bool Silent { get; set; }
        public int RetryAttempt { get; set; }
        public string Target { get; set; }
    }

    public interface INotifier
    {
        string Name { get; }
        Task SendAsync(NotifyMessage msg, CancellationToken ct = default);
    }

    // Extra channels from `notifiers:` (ntfy, Discord, Matrix, webhook, extra Telegram chats).
    // The `telegram:` block is unchanged: workers still post albums there. These channels get the caption and
    // the first snapshot, once per event/review, not once per Telegram chat.
    public static class NotifierHub
    {
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        static List<INotifier> list = new List<INotifier>();
        static readonly object sync = new object();

        public static IReadOnlyList<INotifier> Current { get { lock (sync) return list; } }

        public static void Reload(IEnumerable<NotifierSettings> settings)
        {
            var next = new List<INotifier>();
            foreach (var s in settings ?? new List<NotifierSettings>())
            {
                var n = Create(s, http);
                if (n != null) next.Add(n);
            }
            lock (sync) list = next;
            if (next.Count > 0)
                Program.Log("app", "", "", "Notifiers: " + string.Join(", ", next.Select(n => n.Name)));
        }

        public static INotifier Create(NotifierSettings s, HttpClient client)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.type)) return null;
            return s.type.Trim().ToLowerInvariant() switch
            {
                "ntfy" => string.IsNullOrEmpty(s.url) ? null : new NtfyNotifier(s, client),
                "discord" => string.IsNullOrEmpty(s.url) ? null : new DiscordNotifier(s, client),
                "matrix" => string.IsNullOrEmpty(s.homeserver) || string.IsNullOrEmpty(s.token) || string.IsNullOrEmpty(s.room) ? null : new MatrixNotifier(s, client),
                "webhook" => string.IsNullOrEmpty(s.url) ? null : new WebhookNotifier(s, client),
                "telegram" => s.chatids == null || s.chatids.Count == 0 ? null : new TelegramNotifier(s),
                _ => null
            };
        }

        public static void SendFireAndForget(NotifyMessage msg)
        {
            IReadOnlyList<INotifier> snapshot;
            lock (sync) snapshot = list;
            if (snapshot.Count == 0 || msg == null) return;
            _ = Task.Run(() => SendAllAsync(snapshot, msg));
        }

        public static async Task SendAllAsync(IReadOnlyList<INotifier> notifiers, NotifyMessage msg)
        {
            foreach (var n in notifiers)
            {
                if (msg.Target != null && n.Name != msg.Target) continue;
                try
                {
                    await n.SendAsync(msg);
                }
                catch (Exception ex)
                {
                    Program.Log("app", msg.Id, msg.Camera, "Notifier " + n.Name + " failed: " + ex.Message);
                    Metrics.Inc("lookout_notifier_errors_total", "type", n.Name.Split(':')[0]);
                    if (RetryQueue.Enabled && RetryQueue.IsTransient(ex))
                    {
                        var copy = Clone(msg);
                        copy.Target = n.Name;
                        RetryQueue.TrySchedule("notify", copy, copy.RetryAttempt, msg.Id, msg.Camera, ex);
                    }
                }
            }
        }

        static NotifyMessage Clone(NotifyMessage m) => new NotifyMessage
        {
            Kind = m.Kind, Id = m.Id, Camera = m.Camera, Body = m.Body,
            SnapshotPaths = m.SnapshotPaths == null ? new List<string>() : new List<string>(m.SnapshotPaths),
            Silent = m.Silent, RetryAttempt = m.RetryAttempt, Target = m.Target
        };

        internal static string FirstSnapshot(NotifyMessage msg) =>
            msg.SnapshotPaths?.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

        internal static string TitleOf(NotifierSettings s, NotifyMessage msg) =>
            string.IsNullOrEmpty(s.title) ? "Lookout · " + (msg.Camera ?? msg.Kind) : s.title;
    }

    internal partial class Program
    {
        internal static void ExtraNotify(string kind, string id, string camera, string body, IEnumerable<string> snapshots)
        {
            NotifierHub.SendFireAndForget(new NotifyMessage
            {
                Kind = kind,
                Id = id,
                Camera = camera,
                Body = body,
                SnapshotPaths = snapshots?.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).ToList() ?? new List<string>(),
                Silent = NotifySilent
            });
        }
    }
}
