using Xunit;

namespace Lookout.Tests
{
    [Collection(ProgramStateCollection.Name)]
    public class NotificationTests : IDisposable
    {
        readonly string dir;
        DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        public NotificationTests()
        {
            TestEnv.Init();
            dir = Path.Combine(Path.GetTempPath(), "lookout-notify-" + Guid.NewGuid().ToString("N"));
            MuteService.FilePath = Path.Combine(dir, "mute.json");
            MuteService.Clock = () => now;
            MuteService.Reset();
            NotificationGate.Clock = () => now;
            NotificationGate.Reset();
            Metrics.Reset();
        }

        public void Dispose()
        {
            MuteService.FilePath = MuteService.DefaultFile;
            MuteService.Clock = () => DateTime.UtcNow;
            MuteService.Reset();
            NotificationGate.Clock = () => DateTime.UtcNow;
            try { Directory.Delete(dir, true); } catch { }
        }

        // ---- quiet hours ----------------------------------------------------------------------------------------------

        static QuietSettings Q(string from, string to, string mode = null) =>
            mode == null ? new QuietSettings { from = from, to = to } : new QuietSettings { from = from, to = to, mode = mode };

        [Theory]
        [InlineData("22:00", "07:00", "23:30", true)]    // crosses midnight
        [InlineData("22:00", "07:00", "03:00", true)]
        [InlineData("22:00", "07:00", "07:00", false)]   // the end is exclusive
        [InlineData("22:00", "07:00", "12:00", false)]
        [InlineData("22:00", "07:00", "22:00", true)]
        [InlineData("09:00", "17:30", "12:00", true)]    // same day
        [InlineData("09:00", "17:30", "17:30", false)]
        [InlineData("9:00", "17:30", "9:00", true)]
        [InlineData("10:00", "10:00", "10:00", false)]   // empty interval
        [InlineData("soon", "17:30", "12:00", false)]    // unparsable: no quiet hours
        [InlineData(null, null, "12:00", false)]
        public void IsActive_FollowsTheInterval(string from, string to, string at, bool expected)
        {
            Assert.Equal(expected, QuietHours.IsActive(Q(from, to), TimeSpan.Parse(at)));
        }

        [Fact]
        public void IsActive_NoBlock_IsFalse() => Assert.False(QuietHours.IsActive(null, TimeSpan.Zero));

        [Theory]
        [InlineData(null, QuietMode.Silent)]
        [InlineData("silent", QuietMode.Silent)]
        [InlineData("SNAPSHOT", QuietMode.Snapshot)]
        [InlineData("none", QuietMode.None)]
        [InlineData("whatever", QuietMode.Silent)]
        public void ParseMode(string text, QuietMode expected) => Assert.Equal(expected, QuietHours.ParseMode(text));

        [Fact]
        public void Current_UsesTimeOffsetAndPrefersTheCamera()
        {
            var global = Q("22:00", "07:00", "silent");
            var cam = new Camera { camera = "front" };
            var camQuiet = new Camera { camera = "yard", quiet = Q("12:00", "13:00", "none") };

            var utcMorning = new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc);   // 08:00 at +180 min: not quiet; 05:00 at 0
            Assert.Equal(QuietMode.Off, QuietHours.Current(cam, global, utcMorning, 180));
            Assert.Equal(QuietMode.Silent, QuietHours.Current(cam, global, utcMorning, 0));

            // The camera's own block replaces the global one.
            Assert.Equal(QuietMode.Off, QuietHours.Current(camQuiet, global, utcMorning, 0));
            Assert.Equal(QuietMode.None, QuietHours.Current(camQuiet, global, new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc), 0));
            Assert.Equal(QuietMode.Off, QuietHours.Current(cam, null, utcMorning, 0));
        }

        // ---- cooldown -------------------------------------------------------------------------------------------------

        static readonly string[] Person = { "person" };

        [Fact]
        public void Cooldown_Zero_AllowsEverything()
        {
            var cam = new Camera { camera = "front", cooldown = 0 };
            Assert.True(NotificationGate.Allow(cam, "e1", Person));
            Assert.True(NotificationGate.Allow(cam, "e2", Person));
        }

        [Fact]
        public void Cooldown_BlocksTheCameraUntilItIsOver()
        {
            var cam = new Camera { camera = "front", cooldown = 5 };
            Assert.True(NotificationGate.Allow(cam, "e1", Person));

            now = now.AddMinutes(4);
            Assert.False(NotificationGate.Allow(cam, "e2", new[] { "car" }));         // another object, but not per object

            now = now.AddMinutes(1);
            Assert.True(NotificationGate.Allow(cam, "e3", Person));
        }

        [Fact]
        public void Cooldown_OtherCamerasAreNotAffected()
        {
            Assert.True(NotificationGate.Allow(new Camera { camera = "front", cooldown = 5 }, "e1", Person));
            Assert.True(NotificationGate.Allow(new Camera { camera = "yard", cooldown = 5 }, "e2", Person));
        }

        [Fact]
        public void Cooldown_PerObject_OnlyBlocksTheSameObject()
        {
            var cam = new Camera { camera = "front", cooldown = 5, cooldownperobject = true };
            Assert.True(NotificationGate.Allow(cam, "e1", Person));

            now = now.AddMinutes(1);
            Assert.False(NotificationGate.Allow(cam, "e2", Person));
            Assert.True(NotificationGate.Allow(cam, "e3", new[] { "car" }));
            Assert.False(NotificationGate.Allow(cam, "e4", new[] { "car" }));
        }

        [Fact]
        public void Cooldown_ReviewWithSeveralObjects_PassesWhenOneIsNew()
        {
            var cam = new Camera { camera = "front", cooldown = 5, cooldownperobject = true };
            Assert.True(NotificationGate.Allow(cam, "r1", Person));
            Assert.False(NotificationGate.Allow(cam, "r2", Person));
            Assert.True(NotificationGate.Allow(cam, "r3", new[] { "person", "dog" }));
        }

        [Fact]
        public void Cooldown_LaterMessagesOfTheSameEventPass()
        {
            var cam = new Camera { camera = "front", cooldown = 5 };
            Assert.True(NotificationGate.Allow(cam, "e1", Person));    // new
            Assert.True(NotificationGate.Allow(cam, "e1", Person));    // update
            Assert.True(NotificationGate.Allow(cam, "e1", Person));    // end
            Assert.False(NotificationGate.Allow(cam, "e2", Person));
        }

        // ---- mute -----------------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("30m", 30)]
        [InlineData("2h", 120)]
        [InlineData("1d", 1440)]
        [InlineData("45", 45)]
        [InlineData("2ч", 120)]
        [InlineData("3д", 4320)]
        public void Duration_Parses(string text, int minutes)
        {
            Assert.True(MuteService.TryParseDuration(text, out var d));
            Assert.Equal(TimeSpan.FromMinutes(minutes), d);
        }

        [Theory]
        [InlineData("")]
        [InlineData("0h")]
        [InlineData("abc")]
        [InlineData("1.5h")]
        [InlineData("9999d")]
        public void Duration_RejectsGarbage(string text) => Assert.False(MuteService.TryParseDuration(text, out _));

        [Fact]
        public void Mute_OneCamera_ExpiresOnTime()
        {
            MuteService.Mute("front", TimeSpan.FromHours(1));

            Assert.True(MuteService.IsMuted("front"));
            Assert.False(MuteService.IsMuted("yard"));

            now = now.AddMinutes(61);
            Assert.False(MuteService.IsMuted("front"));
        }

        [Fact]
        public void Mute_All_CoversEveryCamera()
        {
            MuteService.Mute(null, TimeSpan.FromMinutes(10));
            Assert.True(MuteService.IsMuted("front"));
            Assert.True(MuteService.IsMuted("anything"));
        }

        [Fact]
        public void Unmute_OneCameraWhileAllAreMuted_KeepsTheOthersMuted()
        {
            MuteService.Mute(null, TimeSpan.FromHours(1));

            Assert.True(MuteService.Unmute("front", new[] { "front", "yard", "garage" }));

            Assert.False(MuteService.IsMuted("front"));
            Assert.True(MuteService.IsMuted("yard"));
            Assert.True(MuteService.IsMuted("garage"));
        }

        [Fact]
        public void Unmute_Everything_AndNothingToLift()
        {
            Assert.False(MuteService.Unmute(null, new[] { "front" }));
            MuteService.Mute("front", TimeSpan.FromHours(1));
            Assert.True(MuteService.Unmute(null, new[] { "front" }));
            Assert.False(MuteService.IsMuted("front"));
        }

        [Fact]
        public void Mute_SurvivesARestart()
        {
            MuteService.Mute("front", TimeSpan.FromHours(2));

            MuteService.Reset();     // a new process reads the file again

            Assert.True(MuteService.IsMuted("front"));
            Assert.Single(MuteService.Active());
        }

        // ---- dispatch -------------------------------------------------------------------------------------------------

        [Fact]
        public void AllowDispatch_ChecksMuteQuietAndCooldown_AndCountsTheSkips()
        {
            var cam = new Camera { camera = "front", cooldown = 5 };

            Assert.True(Program.AllowDispatch(cam, "event", "end", "e1", Person));
            Assert.False(Program.AllowDispatch(cam, "event", "end", "e2", Person));
            Assert.Equal(1, Metrics.Get("lookout_events_skipped_total", "reason", "cooldown"));

            MuteService.Mute("front", TimeSpan.FromHours(1));
            Assert.False(Program.AllowDispatch(cam, "event", "update", "e1", Person));
            Assert.Equal(1, Metrics.Get("lookout_events_skipped_total", "reason", "muted"));

            MuteService.Unmute(null, new[] { "front" });
            var night = new Camera { camera = "yard", quiet = Q("00:00", "23:59", "none") };
            Assert.False(Program.AllowDispatch(night, "review", "end", "r1", Person));
            Assert.Equal(1, Metrics.Get("lookout_events_skipped_total", "reason", "quiet"));
        }

        [Fact]
        public void QuietSnapshotMode_StripsClipAndGifFromTheCamera()
        {
            Program.settings.frigate.cameras = new List<Camera> { new Camera { camera = "front", clip = true, gif = true, snapshot = true, quiet = Q("00:00", "23:59", "snapshot") } };
            Program.settings.options.timeoffset = 0;

            var ctx = NotifyContext.Begin(Program.settings.frigate.cameras[0], "e1");
            var cam = Program.Cam(0);

            Assert.True(ctx.NoClip);
            Assert.False(cam.clip);
            Assert.False(cam.gif);
            Assert.True(cam.snapshot);
            Assert.True(Program.settings.frigate.cameras[0].clip);     // the config itself is untouched
        }

        [Fact]
        public void QuietSilentMode_SetsSilentNotification()
        {
            Program.settings.frigate.cameras = new List<Camera> { new Camera { camera = "front", quiet = Q("00:00", "23:59") } };

            var ctx = NotifyContext.Begin(Program.settings.frigate.cameras[0], "e1");

            Assert.True(ctx.Silent);
            Assert.False(ctx.NoClip);
        }

        [Fact]
        public void Duration_IsFormatted()
        {
            Assert.Equal("30 min", Program.FormatDuration(TimeSpan.FromMinutes(30)));
            Assert.Equal("90 min", Program.FormatDuration(TimeSpan.FromMinutes(90)));
            Assert.Equal("2 h", Program.FormatDuration(TimeSpan.FromHours(2)));
            Assert.Equal("3 d", Program.FormatDuration(TimeSpan.FromDays(3)));
        }

        [Fact]
        public void GetReviewDetections_ReadsTheReviewData()
        {
            string db = TestEnv.CreateFrigateDb();
            try
            {
                Program.settings.frigate.dbpath = db;
                TestEnv.AddReview(db, "r1", "front", "alert", TestEnv.Now(), "{\"detections\":[\"ev1\",\"ev2\"],\"objects\":[\"person\"]}");

                Assert.Equal(new[] { "ev1", "ev2" }, StatsService.GetReviewDetections("r1"));
                Assert.Empty(StatsService.GetReviewDetections("nope"));
            }
            finally { TestEnv.DeleteDb(db); }
        }
    }
}
