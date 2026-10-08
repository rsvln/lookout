using Xunit;

namespace Lookout.Tests
{
    [Collection(ProgramStateCollection.Name)]
    public class StatsServiceTests : IDisposable
    {
        readonly string db;

        public StatsServiceTests()
        {
            db = TestEnv.CreateFrigateDb();
            TestEnv.Init(db);
            Program.settings.frigate.cameras = new List<Camera>
            {
                new Camera { camera = "front", objects = new List<Objects> { new Objects { label = "person", percent = 50 } } },
                new Camera { camera = "yard" },
            };
        }

        public void Dispose() => TestEnv.DeleteDb(db);

        [Fact]
        public void GetEvent_ReadsRowAndZones()
        {
            TestEnv.AddEvent(db, "e1", "front", "person", 0.7, TestEnv.Now(-60), TestEnv.Now(-30), zones: "[\"porch\",\"lawn\"]");

            var ev = StatsService.GetEvent("e1");

            Assert.NotNull(ev);
            Assert.Equal("front", ev.camera);
            Assert.Equal("person", ev.label);
            Assert.Equal(0.7, ev.score, 3);
            Assert.Equal(new[] { "porch", "lawn" }, ev.zones);
            Assert.True(ev.has_clip);
            Assert.NotNull(ev.end_time);
        }

        [Fact]
        public void GetEvent_Unknown_ReturnsNull()
        {
            Assert.Null(StatsService.GetEvent("nope"));
        }

        [Fact]
        public void GetLast_SkipsFalsePositivesAndOrdersNewestFirst()
        {
            TestEnv.AddEvent(db, "old", "front", "person", 0.9, TestEnv.Now(-300));
            TestEnv.AddEvent(db, "new", "front", "person", 0.9, TestEnv.Now(-100));
            TestEnv.AddEvent(db, "fp", "front", "person", 0.9, TestEnv.Now(-10), falsePositive: true);
            TestEnv.AddEvent(db, "other", "yard", "car", 0.9, TestEnv.Now(-50));

            var rows = StatsService.GetLast("front", null, 10);

            Assert.Equal(new[] { "new", "old" }, rows.Select(r => r.id));
            Assert.Equal(new[] { "other" }, StatsService.GetLast("yard", "car", 10).Select(r => r.id));
            Assert.Single(StatsService.GetLast("front", null, 1));
        }

        [Fact]
        public void GetLastPerCamera_ReturnsLatestOfEveryCamera()
        {
            TestEnv.AddEvent(db, "f1", "front", "person", 0.9, TestEnv.Now(-300));
            TestEnv.AddEvent(db, "f2", "front", "person", 0.9, TestEnv.Now(-100));
            TestEnv.AddEvent(db, "y1", "yard", "car", 0.9, TestEnv.Now(-50));

            var rows = StatsService.GetLastPerCamera(1);

            Assert.Equal(new[] { "y1", "f2" }, rows.Select(r => r.id));
        }

        [Fact]
        public void GetStats_CountsEventsHoursAndReviews()
        {
            TestEnv.AddEvent(db, "a", "front", "person", 0.9, TestEnv.Now(-3600));
            TestEnv.AddEvent(db, "b", "front", "car", 0.9, TestEnv.Now(-1800));
            TestEnv.AddEvent(db, "c", "yard", "person", 0.9, TestEnv.Now(-900));
            TestEnv.AddEvent(db, "fp", "yard", "person", 0.9, TestEnv.Now(-900), falsePositive: true);
            TestEnv.AddReview(db, "r1", "front", "alert", TestEnv.Now(-1000));
            TestEnv.AddReview(db, "r2", "front", "detection", TestEnv.Now(-1000));
            TestEnv.AddReview(db, "r3", "yard", "alert", TestEnv.Now(-1000));

            var st = StatsService.GetStats("24h", null, null);

            Assert.Equal(3, st.total);
            Assert.Equal(2, st.alerts);
            Assert.Equal(1, st.detections);
            Assert.Equal(new[] { "front", "yard" }, st.cameras);
            Assert.Equal(2, st.labelTotals["person"]);
            Assert.Equal(1, st.matrix["front"]["car"]);
            Assert.Equal(3, st.hours.Sum());
            Assert.Equal(3, st.days.Sum(d => d.count));
        }

        [Fact]
        public void GetStats_FiltersByCameraAndLabel()
        {
            TestEnv.AddEvent(db, "a", "front", "person", 0.9, TestEnv.Now(-3600));
            TestEnv.AddEvent(db, "b", "front", "car", 0.9, TestEnv.Now(-1800));
            TestEnv.AddEvent(db, "c", "yard", "person", 0.9, TestEnv.Now(-900));

            Assert.Equal(2, StatsService.GetStats("24h", "front", null).total);
            Assert.Equal(2, StatsService.GetStats("24h", null, "person").total);
            Assert.Equal(1, StatsService.GetStats("24h", "front", "person").total);
        }

        [Fact]
        public void GetStats_ConfigOnly_AppliesCameraThresholds()
        {
            TestEnv.AddEvent(db, "ok", "front", "person", 0.8, TestEnv.Now(-3600));
            TestEnv.AddEvent(db, "weak", "front", "person", 0.2, TestEnv.Now(-3000));
            TestEnv.AddEvent(db, "car", "front", "car", 0.9, TestEnv.Now(-2000));
            TestEnv.AddEvent(db, "yard", "yard", "car", 0.1, TestEnv.Now(-1000));
            TestEnv.AddEvent(db, "unlisted", "garage", "person", 0.9, TestEnv.Now(-500));

            var st = StatsService.GetStats("24h", null, null, configOnly: true);

            Assert.Equal(2, st.total);   // front/person 80% and yard/car (no filter on yard)
            Assert.DoesNotContain("garage", st.cameras);
        }

        [Fact]
        public void GetPeriodEvents_ReportsTotalBeforeLimit()
        {
            for (int i = 0; i < 5; i++)
                TestEnv.AddEvent(db, "e" + i, "front", "person", 0.9, TestEnv.Now(-100 * (i + 1)));

            var rows = StatsService.GetPeriodEvents("24h", null, null, false, null, null, 2, out int total);

            Assert.Equal(5, total);
            Assert.Equal(new[] { "e0", "e1" }, rows.Select(r => r.id));
        }

        [Fact]
        public void GetMeta_ListsCamerasAndLabels()
        {
            TestEnv.AddEvent(db, "a", "front", "person", 0.9, TestEnv.Now(-100));
            TestEnv.AddEvent(db, "b", "yard", "car", 0.9, TestEnv.Now(-100));

            var meta = StatsService.GetMeta();

            Assert.Equal(new[] { "front", "yard" }, meta.cameras.OrderBy(c => c));
            Assert.Equal(new[] { "car", "person" }, meta.labels.OrderBy(l => l));
        }

        [Theory]
        [InlineData("24h", true)]
        [InlineData("7d", true)]
        [InlineData("today", true)]
        [InlineData("12ч", true)]
        [InlineData("", true)]
        [InlineData("0h", false)]
        [InlineData("abc", false)]
        public void TryParsePeriod_Recognizes(string period, bool expected)
        {
            Assert.Equal(expected, StatsService.TryParsePeriod(period, out _, out _));
        }

        [Fact]
        public void ParseArgs_SplitsCameraLabelLimitAndPeriod()
        {
            TestEnv.AddEvent(db, "a", "front", "person", 0.9, TestEnv.Now(-100));

            var f = StatsService.ParseArgs(new[] { "front", "PERSON", "5", "7d", "wat" });

            Assert.Equal("front", f.camera);
            Assert.Equal("person", f.label);
            Assert.Equal(5, f.limit);
            Assert.Equal("7d", f.period);
            Assert.Equal(new[] { "wat" }, f.unknown);
        }
    }
}
