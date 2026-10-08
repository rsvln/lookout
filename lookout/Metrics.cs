using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Lookout
{
    // Prometheus metrics in the text exposition format, written by hand to avoid a dependency (GET /metrics).
    // Counters and histograms are updated from anywhere with Inc / Observe; queue lengths are read when scraped.
    public static class Metrics
    {
        const string TrueEndWait = "lookout_trueend_wait_seconds";
        static readonly double[] TrueEndBuckets = { 1, 5, 10, 30, 60, 120, 300, 600, 1200 };

        // series key ("name" or "name{a=\"b\"}") -> value; help text and type per metric name
        static readonly ConcurrentDictionary<string, long> counters = new ConcurrentDictionary<string, long>();
        static readonly Dictionary<string, string> help = new Dictionary<string, string>
        {
            ["lookout_events_total"] = "Frigate events and reviews that passed the filters and went to a worker.",
            ["lookout_events_skipped_total"] = "Messages dropped because the camera is muted, in quiet hours or in its cooldown.",
            ["lookout_worker_errors_total"] = "Workers that ended with an error.",
            ["lookout_telegram_errors_total"] = "Failed Telegram API calls.",
            ["lookout_telegram_rate_limited_total"] = "Telegram calls that hit the 429 rate limit.",
            ["lookout_ollama_errors_total"] = "Failed Ollama (AI) requests.",
            ["lookout_compreface_errors_total"] = "Failed CompreFace (face recognition) requests.",
            ["lookout_retry_scheduled_total"] = "Jobs written to the retry queue.",
            ["lookout_retry_given_up_total"] = "Jobs dropped after options.retrymax attempts.",
        };

        static readonly object histLock = new object();
        static long[] histBuckets = new long[TrueEndBuckets.Length];
        static long histCount;
        static double histSum;

        // Label pairs go as name, value, name, value ...
        public static void Inc(string name, params string[] labels) => counters.AddOrUpdate(Key(name, labels), 1, (_, v) => v + 1);

        public static long Get(string name, params string[] labels) => counters.TryGetValue(Key(name, labels), out var v) ? v : 0;

        // How long a worker waited for Frigate's recordings of an ended event/review (the "true end").
        public static void Observe(double seconds)
        {
            lock (histLock)
            {
                for (int i = 0; i < TrueEndBuckets.Length; i++)
                    if (seconds <= TrueEndBuckets[i])
                        histBuckets[i]++;
                histCount++;
                histSum += seconds;
            }
        }

        public static void Reset()
        {
            counters.Clear();
            lock (histLock)
            {
                histBuckets = new long[TrueEndBuckets.Length];
                histCount = 0;
                histSum = 0;
            }
        }

        static string Key(string name, string[] labels)
        {
            if (labels == null || labels.Length == 0)
                return name;
            var sb = new StringBuilder(name).Append('{');
            for (int i = 0; i + 1 < labels.Length; i += 2)
            {
                if (i > 0) sb.Append(',');
                sb.Append(labels[i]).Append("=\"").Append(labels[i + 1].Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")).Append('"');
            }
            return sb.Append('}').ToString();
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public static string Render()
        {
            var sb = new StringBuilder();

            foreach (var group in counters.GroupBy(kv => kv.Key.Contains('{') ? kv.Key.Substring(0, kv.Key.IndexOf('{')) : kv.Key).OrderBy(g => g.Key))
            {
                sb.Append("# HELP ").Append(group.Key).Append(' ').Append(help.GetValueOrDefault(group.Key, group.Key)).Append('\n');
                sb.Append("# TYPE ").Append(group.Key).Append(" counter\n");
                foreach (var kv in group.OrderBy(k => k.Key))
                    sb.Append(kv.Key).Append(' ').Append(kv.Value).Append('\n');
            }

            lock (histLock)
            {
                sb.Append("# HELP ").Append(TrueEndWait).Append(" Seconds spent waiting for Frigate's recordings after an event or review ended.\n");
                sb.Append("# TYPE ").Append(TrueEndWait).Append(" histogram\n");
                for (int i = 0; i < TrueEndBuckets.Length; i++)
                    sb.Append(TrueEndWait).Append("_bucket{le=\"").Append(F(TrueEndBuckets[i])).Append("\"} ").Append(histBuckets[i]).Append('\n');
                sb.Append(TrueEndWait).Append("_bucket{le=\"+Inf\"} ").Append(histCount).Append('\n');
                sb.Append(TrueEndWait).Append("_sum ").Append(F(histSum)).Append('\n');
                sb.Append(TrueEndWait).Append("_count ").Append(histCount).Append('\n');
            }

            Gauge(sb, "lookout_ai_queue_length", "Tasks waiting in the AI (Ollama) queue.", Program.aiQueue?.GetQueueSize() ?? 0);
            Gauge(sb, "lookout_fr_queue_length", "Tasks waiting in the face recognition (CompreFace) queue.", Program.frQueue?.GetQueueSize() ?? 0);
            Gauge(sb, "lookout_retry_queue_length", "Jobs waiting in the retry queue on disk.", RetryQueue.Count());
            Gauge(sb, "lookout_mqtt_connected", "1 when connected to the MQTT broker.", Program.mqttClient?.IsConnected == true ? 1 : 0);
            Gauge(sb, "lookout_info", "Build information.", 1, "version=\"" + VersionInfo.Version + "\"");
            return sb.ToString();
        }

        static void Gauge(StringBuilder sb, string name, string helpText, double value, string labels = null)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(helpText).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name);
            if (labels != null) sb.Append('{').Append(labels).Append('}');
            sb.Append(' ').Append(F(value)).Append('\n');
        }
    }
}
