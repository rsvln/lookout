using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace Lookout
{
    // /mute and the "Mute 1 h" button: a muted camera sends nothing until the time is up. "*" stands for every camera.
    // The state is kept in a file, so a restart does not unmute anything.
    public static class MuteService
    {
        public const string All = "*";
        public const string DefaultFile = "/var/log/lookout/mute.json";

        public static string FilePath { get; set; } = DefaultFile;
        public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        static readonly object sync = new object();
        static Dictionary<string, DateTime> until;     // camera -> UTC time the mute ends
        static string loadedFrom;

        // "30m", "2h", "1d" (also Russian м/ч/д); a bare number is minutes.
        public static bool TryParseDuration(string text, out TimeSpan duration)
        {
            duration = default;
            var m = Regex.Match((text ?? "").Trim().ToLowerInvariant(), @"^(\d{1,4})([mhdмчд]?)$");
            if (!m.Success) return false;
            int n = int.Parse(m.Groups[1].Value);
            if (n <= 0) return false;
            duration = m.Groups[2].Value switch
            {
                "h" or "ч" => TimeSpan.FromHours(n),
                "d" or "д" => TimeSpan.FromDays(n),
                _ => TimeSpan.FromMinutes(n),
            };
            return duration <= TimeSpan.FromDays(365);
        }

        static void Load()
        {
            if (until != null && loadedFrom == FilePath) return;
            loadedFrom = FilePath;
            until = new Dictionary<string, DateTime>();
            try
            {
                if (System.IO.File.Exists(FilePath))
                    until = JsonConvert.DeserializeObject<Dictionary<string, DateTime>>(System.IO.File.ReadAllText(FilePath)) ?? until;
            }
            catch (Exception ex)
            {
                Program.Log("app", "", "", "Cannot read " + FilePath + ": " + ex.Message);
            }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                System.IO.File.WriteAllText(FilePath, JsonConvert.SerializeObject(until, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Program.Log("app", "", "", "Cannot write " + FilePath + ": " + ex.Message);
            }
        }

        static void Expire()
        {
            var now = Clock();
            foreach (var key in until.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                until.Remove(key);
        }

        // Forgets everything held in memory, so the next call reads the file again (tests).
        public static void Reset()
        {
            lock (sync) { until = null; loadedFrom = null; }
        }

        // camera == null mutes every camera.
        public static DateTime Mute(string camera, TimeSpan duration)
        {
            lock (sync)
            {
                Load();
                Expire();
                var end = Clock() + duration;
                until[camera ?? All] = end;
                Save();
                return end;
            }
        }

        // camera == null lifts every mute. Lifting one camera while all are muted leaves the other cameras muted.
        // Returns false when there was nothing to lift.
        public static bool Unmute(string camera, IEnumerable<string> allCameras)
        {
            lock (sync)
            {
                Load();
                Expire();
                if (camera == null)
                {
                    bool any = until.Count > 0;
                    until.Clear();
                    if (any) Save();
                    return any;
                }

                bool removed = until.Remove(camera);
                if (until.TryGetValue(All, out var allUntil))
                {
                    until.Remove(All);
                    foreach (var other in allCameras.Where(c => !c.Equals(camera, StringComparison.OrdinalIgnoreCase)))
                        until.TryAdd(other, allUntil);
                    removed = true;
                }
                if (removed) Save();
                return removed;
            }
        }

        public static bool IsMuted(string camera)
        {
            lock (sync)
            {
                Load();
                var now = Clock();
                return (until.TryGetValue(All, out var a) && a > now) || (camera != null && until.TryGetValue(camera, out var c) && c > now);
            }
        }

        public static Dictionary<string, DateTime> Active()
        {
            lock (sync)
            {
                Load();
                Expire();
                return new Dictionary<string, DateTime>(until);
            }
        }
    }
}
