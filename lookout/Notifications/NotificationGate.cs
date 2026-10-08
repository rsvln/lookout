using System.Collections.Concurrent;

namespace Lookout
{
    // Camera `cooldown` (minutes) and `cooldownperobject`: a camera that has just sent a notification stays quiet.
    // Several messages about one event or review (new, update, end) are one notification: once an id has passed,
    // its later messages pass too.
    public static class NotificationGate
    {
        public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        static readonly object sync = new object();
        static readonly Dictionary<string, DateTime> lastSent = new Dictionary<string, DateTime>();
        static readonly Dictionary<string, DateTime> seenIds = new Dictionary<string, DateTime>();
        static readonly TimeSpan IdMemory = TimeSpan.FromHours(6);

        public static void Reset()
        {
            lock (sync)
            {
                lastSent.Clear();
                seenIds.Clear();
            }
        }

        // `labels`: what the notification is about (an event has one, a review several).
        public static bool Allow(Camera cam, string id, IEnumerable<string> labels)
        {
            if (cam == null || cam.cooldown <= 0)
                return true;

            var now = Clock();
            var window = TimeSpan.FromMinutes(cam.cooldown);
            string idKey = cam.camera + "|" + id;

            lock (sync)
            {
                foreach (var old in seenIds.Where(kv => now - kv.Value > IdMemory).Select(kv => kv.Key).ToList())
                    seenIds.Remove(old);

                if (id != null && seenIds.ContainsKey(idKey))
                    return true;

                var keys = new List<string>();
                if (cam.cooldownperobject)
                    keys.AddRange((labels ?? Enumerable.Empty<string>()).Where(l => !string.IsNullOrEmpty(l)).Select(l => cam.camera + "|" + l));
                if (keys.Count == 0)
                    keys.Add(cam.camera);

                // Per object: one object type that is not in its cooldown is enough to send.
                bool allowed = keys.Any(k => !lastSent.TryGetValue(k, out var last) || now - last >= window);
                if (!allowed)
                    return false;

                foreach (var k in keys)
                    lastSent[k] = now;
                if (id != null)
                    seenIds[idKey] = now;
                return true;
            }
        }
    }
}
