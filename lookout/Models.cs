using Telegram.Bot.Types;
using YamlDotNet.Core;

namespace Lookout
{

    public class Queries
    {
        public string getCamerasQuery()
        {
            return @"select distinct camera
                            from recordings 
                            where DATETIME(end_time, 'unixepoch') >= datetime('now','-24 hour') 
                            order by camera;";

        }

        // Recording segments covering an event/review; takes $id and $camera parameters (see Program.RecordingsCommand).
        public string getEventQuery(string entity, bool strong = true)
        {
            if (entity == "review")
                entity = "reviewsegment";

                return @"SELECT r.camera,
                                   r.path,
                                   cast(r.start_time AS real) AS start_time,
                                   cast(r.end_time AS real) AS end_time,
                                   cast(r.duration AS real) AS duration
                            FROM recordings r
                            WHERE r.start_time >=
                                (SELECT rs.start_time
                                 FROM recordings rs
                                 JOIN
                                   (SELECT camera,
                                           start_time
                                    FROM " + entity + @"
                                    WHERE id = $id) s ON rs.camera = s.camera
                                 AND rs.start_time <= s.start_time
                                 ORDER BY rs.start_time DESC
                                 LIMIT 1)
                              AND r.end_time <=
                                (SELECT re.end_time
                                 FROM recordings re
                                 JOIN
                                   (SELECT end_time,
                                           camera
                                    FROM " + entity + @"
                                    WHERE id = $id) e ON re.camera = e.camera
                                 AND e.end_time " + (strong ? "<= re.end_time" : ">= re.start_time") + 
                                 @" ORDER BY re.end_time " + (strong ? "ASC" : "DESC") +
                                 @" LIMIT 1)
                              AND r.camera = $camera
                            ORDER BY r.start_time";

        }

    }

    public class SettingsFile
    {
        public FrigateSettings frigate { get; set; }
        public MqttSettings mqtt { get; set; }
        public TelegramSettings telegram { get; set; }
        //public ffmpegSettings ffmpeg { get; set; }
        public Options options { get; set; }
        public LoggerSettings logger { get; set; }
        public AISettings ai { get; set; }
        public FRSettings fr { get; set; }
        public WebSettings web { get; set; }
    }

    // Optional web UI login (HTTP Basic); when user or password is empty the UI is open.
    public class WebSettings
    {
        public string user { get; set; }
        public string password { get; set; }
        // Address of the web UI as seen from the phone, e.g. https://lookout.example.com (for "Open in Lookout" buttons).
        public string publicurl { get; set; }
        // How the UI asks for a login: "basic" (default; the browser's HTTP Basic prompt) or "form" (login page and a
        // session cookie). Basic credentials keep working in "form" mode, for scripts and Prometheus.
        public string auth { get; set; } = "basic";
        // More accounts besides user/password (which is always an admin): role is "viewer" (default) or "admin".
        public List<WebUser> users { get; set; } = new List<WebUser>();
        // Signs sessions and clip links; without it the key comes from the passwords, so changing one signs everybody out.
        public string secret { get; set; }
        public int sessionhours { get; set; } = 168;
    }

    public class WebUser
    {
        public string user { get; set; }
        public string password { get; set; }
        public string role { get; set; } = "viewer";
    }

    // Quiet hours: between `from` and `to` (local time, HH:mm; the interval may cross midnight) notifications change:
    //   none     - nothing is sent
    //   snapshot - only the snapshot, no clip or GIF
    //   silent   - everything is sent, but without a sound (Telegram's disable_notification)
    public class QuietSettings
    {
        public string from { get; set; }
        public string to { get; set; }
        public string mode { get; set; } = "silent";
    }

    public class Objects
    {
        public string label { get; set; }
        public int percent { get; set; } = 50;

    }

    public class Camera
    {
        public string camera { get; set; }
        public bool snapshot { get; set; } = true;
        public bool clip { get; set; } = false;
        public bool gif { get; set; } = false;
        public bool trueend { get; set; } = false;
        public bool sctogether { get; set; } = false;
        public bool ai { get; set; } = false;
        public bool fr { get; set; } = false;
        public string topic { get; set; } = "reviews";
        public string snapshottrigger { get; set; } = "end";
        public List<Objects> objects { get; set; } = new List<Objects>();
        public List<string> severity { get; set; } = new List<string>() { "detection", "alert" };
        public List<string> zones { get; set; } = new List<string>();
        // Minutes during which a camera that has just sent a notification stays quiet (0 = no cooldown).
        public int cooldown { get; set; } = 0;
        // Count the cooldown per object type: a person right after a car still gets through.
        public bool cooldownperobject { get; set; } = false;
        // Quiet hours of this camera; replaces options.quiet.
        public QuietSettings quiet { get; set; }

        internal Camera Clone() => (Camera)MemberwiseClone();
    }

    public class FrigateSettings
    {
        public string host { get; set; }
        public int port { get; set; }
        public string clipspath { get; set; }
        public string dbpath { get; set; }
        public string recordingspath { get; set; }
        public string recordingsoriginalpath { get; set; }
        public List<Camera> cameras { get; set; } 
    }
    public class MqttSettings
    {
        public string host { get; set; }
        public int port { get; set; }
        public string user { get; set; }
        public string password { get; set; }
        public string eventstopic { get; set; } = "frigate/events";
        public string reviewstopic { get; set; } = "frigate/reviews";
    }

    public class TelegramSettings
    {
        public string token { get; set; }
        public List<string> chatids { get; set; }
        public long clipsizecheck { get; set; }
        public long clipsizesplit { get; set; }
        public int mediagrouplimit { get; set; } = 10;
        public int sendchatstimepause { get; set; } = 30;
        public int retryonratelimit { get; set; } = 30;
        public string apiserver { get; set; } = "https://api.telegram.org/";
    }
    /*
    public class ffmpegSettings
    {
        public string path { get; set; }
    }
    */
    public class Options
    {
        public int timeoffset { get; set; } = 180;
        public int timeout { get; set; } = 300;
        public int retry { get; set; } = 10;
        public bool sendeverythingwhatyouhave { get; set; } = true;
        public int gifwidth { get; set; } = 640;
        // Retry queue for Telegram / Ollama / CompreFace outages: how many times a failed job is repeated (0 = off)
        // and the first delay in seconds, doubled for every next repeat.
        public int retrymax { get; set; } = 0;
        public int retrybackoff { get; set; } = 30;
        // Quiet hours for every camera that has no `quiet` of its own.
        public QuietSettings quiet { get; set; }
        // Buttons under each notification: Clip, Open in Lookout (needs web.publicurl), Mute the camera for an hour.
        public bool buttons { get; set; } = false;
        public LocaleSettings locale { get; set; } = new LocaleSettings();
    }

    // options.locale: one language for everything ("locale: ru") or one per area:
    //   locale:
    //     web: en
    //     telegram: ru
    //     ai: ru
    // Areas left out are English.
    public class LocaleSettings : YamlDotNet.Serialization.IYamlConvertible
    {
        public string web { get; set; } = "en";
        public string telegram { get; set; } = "en";
        public string ai { get; set; } = "en";
        // The AI language was given explicitly (as locale.ai); only then prompts get "answer in <language>".
        public bool aiExplicit { get; private set; }

        class Areas
        {
            public string web { get; set; }
            public string telegram { get; set; }
            public string ai { get; set; }
        }

        public void Read(YamlDotNet.Core.IParser parser, Type expectedType, YamlDotNet.Serialization.ObjectDeserializer nestedObjectDeserializer)
        {
            if (parser.TryConsume<YamlDotNet.Core.Events.Scalar>(out var scalar))
            {
                web = telegram = ai = string.IsNullOrWhiteSpace(scalar.Value) ? "en" : scalar.Value.Trim();
                return;
            }
            var areas = (Areas)nestedObjectDeserializer(typeof(Areas)) ?? new Areas();
            web = string.IsNullOrWhiteSpace(areas.web) ? "en" : areas.web.Trim();
            telegram = string.IsNullOrWhiteSpace(areas.telegram) ? "en" : areas.telegram.Trim();
            ai = string.IsNullOrWhiteSpace(areas.ai) ? "en" : areas.ai.Trim();
            aiExplicit = !string.IsNullOrWhiteSpace(areas.ai);
        }

        public void Write(YamlDotNet.Core.IEmitter emitter, YamlDotNet.Serialization.ObjectSerializer nestedObjectSerializer) =>
            nestedObjectSerializer(new Areas { web = web, telegram = telegram, ai = ai });
    }

    public class LoggerSettings
    {
        public bool file { get; set; }
        public bool console { get; set; }
    }

    public class AISettings
    {
        public string url { get; set; }
        public string model { get; set; }
        public string humanprompt { get; set; }
        public string nonhumanprompt { get; set; }
        public int numpredict { get; set; } = 150;
        public double temperature { get; set; } = 0.1;
        public bool thinking { get; set; } = false;
        public int resizetowidth { get; set; } = 640;
    }

    public class FRSettings
    {
        public string url { get; set; }
        public string apikey { get; set; }
        public double confidence { get; set; } = 0.8;
        public double detprobthreshold { get; set; } = 0.8;
    }

    public class BeforeAfterFE
    {
        public string id { get; set; }
        public string camera { get; set; }
        public double frame_time { get; set; }
        public SnapshotFE snapshot { get; set; }
        public string label { get; set; }
        public object sub_label { get; set; }
        public double top_score { get; set; }
        public bool false_positive { get; set; }
        public double start_time { get; set; }
        public Double? end_time { get; set; }
        public double score { get; set; }
        public List<int> box { get; set; }
        public int area { get; set; }
        public double ratio { get; set; }
        public List<int> region { get; set; }
        public bool stationary { get; set; }
        public int motionless_count { get; set; }
        public int position_changes { get; set; }
        public List<object> current_zones { get; set; }
        public List<object> entered_zones { get; set; }
        public bool has_clip { get; set; }
        public bool has_snapshot { get; set; }
        public AttributesFE attributes { get; set; }
        public List<object> current_attributes { get; set; } = new List<object>();
    }

    public class AttributesFE
    {
    }

    public class FrigateEvent
    {
        public BeforeAfterFE before { get; set; }
        public BeforeAfterFE after { get; set; }
        public string type { get; set; }
    }

    public class SnapshotFE
    {
        public double frame_time { get; set; }
        public List<int> box { get; set; }
        public int area { get; set; }
        public List<int> region { get; set; }
        public double score { get; set; }
        public List<object> attributes { get; set; }
    }

    public class AfterBeforeReview
    {
        public string id { get; set; }
        public string camera { get; set; }
        public double start_time { get; set; }
        public Double? end_time { get; set; }
        public string severity { get; set; }
        public string thumb_path { get; set; }
        public DataReview data { get; set; }
    }

    public class DataReview
    {
        public List<string> detections { get; set; }
        public List<string> objects { get; set; }
        public List<string> sub_labels { get; set; }
        public List<string> zones { get; set; }
        public List<string> audio { get; set; }
    }

    public class FrigateReview
    {
        public string type { get; set; }
        public AfterBeforeReview before { get; set; }
        public AfterBeforeReview after { get; set; }
    }


    public class DbRow
    {
        public string path { get; set; }
        public double start_time { get; set; }
        public double end_time { get; set; }
        public double duration { get; set; }
        public string realpath { get; set; }
        public Int64 size { get; set; }
    }

    public class AITask
    {
        public List<string> ImagePaths { get; set; }
        public long ChatId { get; set; }
        public int MessageId { get; set; }
        public string Camera { get; set; }
        public string EventId { get; set; }
        public DateTime QueuedAt { get; set; }
        public string OriginalCaption { get; set; }
        public string Prompt { get; set; }
        // How many times the retry queue has already repeated this task (0 = first run).
        public int RetryAttempt { get; set; }
    }

    public class AIResponse
    {
        public int people_count { get; set; }
        public string description { get; set; }
        public string timestamp { get; set; }
    }

    public class OllamaResponse
    {
        public string response { get; set; }
        public string thinking { get; set; }
    }

    public class OllamaMessage
    {
        public string content { get; set; }
        public string thinking { get; set; }
    }

    public class EventRow
    {
        public string id { get; set; }
        public string camera { get; set; }
        public string label { get; set; }
        public string sub_label { get; set; }
        public double score { get; set; }
        public double start_time { get; set; }
        public double? end_time { get; set; }
        public List<string> zones { get; set; } = new List<string>();
        public bool has_snapshot { get; set; }
        public bool has_clip { get; set; }
    }

    public class MetaResult
    {
        public List<string> cameras { get; set; } = new List<string>();
        public List<string> labels { get; set; } = new List<string>();
    }

    public class CommandFilter
    {
        public string camera { get; set; }
        public string label { get; set; }
        public int? limit { get; set; }
        public string period { get; set; }
        public List<string> unknown { get; set; } = new List<string>();
    }

    public class DayCount
    {
        public string day { get; set; }
        public int count { get; set; }
    }

    public class StatsResult
    {
        public string period { get; set; }
        public double from { get; set; }
        public double to { get; set; }
        public string camera { get; set; }
        public string label { get; set; }
        public bool configOnly { get; set; }
        public int total { get; set; }
        public int alerts { get; set; }
        public int detections { get; set; }
        public int peakHour { get; set; }
        public List<string> cameras { get; set; } = new List<string>();
        public List<string> labels { get; set; } = new List<string>();
        public Dictionary<string, int> labelTotals { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, Dictionary<string, int>> matrix { get; set; } = new Dictionary<string, Dictionary<string, int>>();
        public int[] hours { get; set; } = new int[24];
        public List<DayCount> days { get; set; } = new List<DayCount>();
        public Dictionary<string, double> lastByCamera { get; set; } = new Dictionary<string, double>();
        // Monday = 0 … Sunday = 6, each 24 hours of the local day.
        public int[][] heatmap { get; set; } = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
    }


}
