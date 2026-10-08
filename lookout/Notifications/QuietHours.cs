namespace Lookout
{
    public enum QuietMode { Off, None, Snapshot, Silent }

    // options.quiet / camera.quiet: what to do with notifications at night.
    public static class QuietHours
    {
        public static bool TryParseTime(string text, out TimeSpan time)
        {
            time = default;
            return !string.IsNullOrWhiteSpace(text) && TimeSpan.TryParseExact(text.Trim(), new[] { "h\\:mm", "hh\\:mm" }, null, out time)
                   && time < TimeSpan.FromDays(1);
        }

        // `localNow` is the time of day in the options.timeoffset zone. An interval with from > to crosses midnight;
        // from == to, or a time that does not parse, means no quiet hours.
        public static bool IsActive(QuietSettings q, TimeSpan timeOfDay)
        {
            if (q == null || !TryParseTime(q.from, out var from) || !TryParseTime(q.to, out var to) || from == to)
                return false;
            return from < to
                ? timeOfDay >= from && timeOfDay < to
                : timeOfDay >= from || timeOfDay < to;
        }

        public static QuietMode ParseMode(string mode) => (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "none" => QuietMode.None,
            "snapshot" => QuietMode.Snapshot,
            _ => QuietMode.Silent,
        };

        // The camera's own block wins over the global one.
        public static QuietMode Current(Camera cam, QuietSettings global, DateTime utcNow, int timeOffsetMinutes)
        {
            var q = cam?.quiet ?? global;
            var timeOfDay = utcNow.AddMinutes(timeOffsetMinutes).TimeOfDay;
            return IsActive(q, timeOfDay) ? ParseMode(q.mode) : QuietMode.Off;
        }

        public static QuietMode Current(Camera cam) =>
            Current(cam, Program.settings?.options?.quiet, DateTime.UtcNow, Program.settings?.options?.timeoffset ?? 0);
    }
}
