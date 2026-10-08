namespace Lookout
{
    // What applies to the notification a worker is sending right now: quiet hours (no sound / no clip) and the
    // button message. Set by the worker and seen by everything it calls, through AsyncLocal.
    internal class NotifyContext
    {
        static readonly AsyncLocal<NotifyContext> current = new AsyncLocal<NotifyContext>();
        public static NotifyContext Current => current.Value;

        public string Camera { get; private set; }
        // Event the buttons act on (for a review: its first detection).
        public string ActionEventId { get; private set; }
        public QuietMode Quiet { get; private set; }
        // Chats that already got the button message.
        public HashSet<long> ActionsSent { get; } = new HashSet<long>();

        // "none" never reaches a worker (it is dropped when the message arrives); a retried job meets it as a silent one.
        public bool Silent => Quiet == QuietMode.Silent || Quiet == QuietMode.None;
        public bool NoClip => Quiet == QuietMode.Snapshot;

        public static NotifyContext Begin(Camera cam, string actionEventId)
        {
            var ctx = new NotifyContext { Camera = cam?.camera, ActionEventId = actionEventId, Quiet = QuietHours.Current(cam) };
            current.Value = ctx;
            return ctx;
        }
    }

    internal partial class Program
    {
        static bool NotifySilent => NotifyContext.Current?.Silent == true;

        // The camera as the current notification sees it: in quiet "snapshot" mode without clip and GIF.
        internal static Camera Cam(int cami)
        {
            var cam = settings.frigate.cameras[cami];
            if (NotifyContext.Current?.NoClip == true && (cam.clip || cam.gif))
            {
                cam = cam.Clone();
                cam.clip = false;
                cam.gif = false;
            }
            return cam;
        }

        // Called when a Frigate message has passed the object filters: false if the camera is muted, in quiet hours
        // "none", or in its cooldown. Several messages about one event/review count as one notification.
        internal static bool AllowDispatch(Camera cam, string source, string type, string id, IEnumerable<string> labels)
        {
            string reason = null;
            if (MuteService.IsMuted(cam.camera))
                reason = "muted";
            else if (QuietHours.Current(cam) == QuietMode.None)
                reason = "quiet";
            else if (!NotificationGate.Allow(cam, id, labels))
                reason = "cooldown";

            if (reason == null)
                return true;
            Metrics.Inc("lookout_events_skipped_total", "reason", reason);
            if (type != "update")
                Log(source, id, cam.camera, "Skipped: " + reason);
            return false;
        }
    }
}
