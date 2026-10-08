using Microsoft.Data.Sqlite;
using Xunit;

namespace Lookout.Tests
{
    // Program keeps its settings in static fields, so every test class that touches them joins this collection
    // and the tests run one after another.
    [CollectionDefinition(Name)]
    public class ProgramStateCollection
    {
        public const string Name = "Program state";
    }

    public static class TestEnv
    {
        // A minimal config: console logging off, no network, English locale.
        public static SettingsFile NewSettings(string dbPath = null) => new SettingsFile
        {
            frigate = new FrigateSettings
            {
                host = "127.0.0.1",
                port = 5000,
                dbpath = dbPath,
                clipspath = Path.Combine(Path.GetTempPath(), "lookout-tests-clips"),
                cameras = new List<Camera>()
            },
            mqtt = new MqttSettings(),
            telegram = new TelegramSettings { chatids = new List<string>(), clipsizecheck = 100, clipsizesplit = 60 },
            options = new Options { timeoffset = 0 },
            logger = new LoggerSettings { console = false, file = false },
        };

        public static void Init(string dbPath = null)
        {
            Program.settings = NewSettings(dbPath);
            Program.appLocation = AppContext.BaseDirectory;
            L10n.Load("en", "en", "en");
        }

        // An empty Frigate-like database with just the tables and columns Lookout reads.
        public static string CreateFrigateDb()
        {
            string path = Path.Combine(Path.GetTempPath(), "lookout-test-" + Guid.NewGuid().ToString("N") + ".db");
            using var db = new SqliteConnection("Data Source = " + path);
            db.Open();
            Exec(db, "CREATE TABLE event (id TEXT PRIMARY KEY, camera TEXT, label TEXT, sub_label TEXT, top_score REAL, data TEXT, " +
                     "false_positive INTEGER, start_time REAL, end_time REAL, zones TEXT, has_snapshot INTEGER, has_clip INTEGER)");
            Exec(db, "CREATE TABLE reviewsegment (id TEXT PRIMARY KEY, camera TEXT, severity TEXT, start_time REAL, data TEXT)");
            Exec(db, "CREATE TABLE recordings (path TEXT, camera TEXT, start_time REAL, end_time REAL)");
            return path;
        }

        public static void AddEvent(string dbPath, string id, string camera, string label, double score, double startUnix,
                                    double? endUnix = null, string zones = "[]", bool falsePositive = false)
        {
            using var db = new SqliteConnection("Data Source = " + dbPath);
            db.Open();
            using var cmd = new SqliteCommand("INSERT INTO event (id, camera, label, top_score, false_positive, start_time, end_time, zones, has_snapshot, has_clip) " +
                                              "VALUES ($id, $camera, $label, $score, $fp, $start, $end, $zones, 1, 1)", db);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$camera", camera);
            cmd.Parameters.AddWithValue("$label", label);
            cmd.Parameters.AddWithValue("$score", score);
            cmd.Parameters.AddWithValue("$fp", falsePositive ? 1 : 0);
            cmd.Parameters.AddWithValue("$start", startUnix);
            cmd.Parameters.AddWithValue("$end", (object)endUnix ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$zones", zones);
            cmd.ExecuteNonQuery();
        }

        public static void AddReview(string dbPath, string id, string camera, string severity, double startUnix, string data = null)
        {
            using var db = new SqliteConnection("Data Source = " + dbPath);
            db.Open();
            using var cmd = new SqliteCommand("INSERT INTO reviewsegment (id, camera, severity, start_time, data) VALUES ($id, $camera, $sev, $start, $data)", db);
            cmd.Parameters.AddWithValue("$data", (object)data ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$camera", camera);
            cmd.Parameters.AddWithValue("$sev", severity);
            cmd.Parameters.AddWithValue("$start", startUnix);
            cmd.ExecuteNonQuery();
        }

        static void Exec(SqliteConnection db, string sql)
        {
            using var cmd = new SqliteCommand(sql, db);
            cmd.ExecuteNonQuery();
        }

        public static double Now(double offsetSeconds = 0) =>
            (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds + offsetSeconds;

        public static void DeleteDb(string path)
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { }
        }
    }
}
