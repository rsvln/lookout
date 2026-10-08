using Microsoft.Data.Sqlite;
using Xunit;

namespace Lookout.Tests
{
    [Collection(ProgramStateCollection.Name)]
    public class LocalStoreTests : IDisposable
    {
        readonly string dir;
        readonly string db;
        readonly string frigate;

        public LocalStoreTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "lookout-local-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            db = Path.Combine(dir, "lookout.db");
            frigate = TestEnv.CreateFrigateDb();
            TestEnv.Init(frigate);
            LocalStore.Open(db);
        }

        public void Dispose()
        {
            LocalStore.Close();
            SqliteConnection.ClearAllPools();
            TestEnv.DeleteDb(frigate);
            try { Directory.Delete(dir, true); } catch { }
        }

        [Fact]
        public void Open_CreatesVersionedSchema()
        {
            using var c = new SqliteConnection("Data Source=" + db);
            c.Open();
            using var cmd = new SqliteCommand("SELECT version FROM schema_migrations", c);
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        [Fact]
        public void Record_ThenSearchByAiAndFace()
        {
            LocalStore.Record("e1", "event", "front", "person", 100, 110, new[] { "porch" }, 0.9, "person at the door", null);
            LocalStore.SetAi("e1", "A person in a red jacket");
            LocalStore.SetFaces("e1", "Alex");

            Assert.Equal("e1", Assert.Single(LocalStore.Search("jacket", null, null, null, null, 10)).id);
            Assert.Equal("e1", Assert.Single(LocalStore.Search("Alex", null, null, null, null, 10)).id);
            Assert.Equal("e1", Assert.Single(LocalStore.Search("porch", null, null, null, null, 10)).id);
            Assert.Empty(LocalStore.Search("nobody", null, null, null, null, 10));
        }

        [Fact]
        public void Correlate_GroupsDifferentCamerasInsideTheWindow()
        {
            Program.settings.options.correlate = 30;
            LocalStore.Record("a", "event", "front", "person", 1000, null, null, 0.9, "front", null);
            string incident = LocalStore.LinkIncident("b", "yard", 1010);
            Assert.False(string.IsNullOrEmpty(incident));
            Assert.Equal("front", LocalStore.OtherCameras(incident, "yard"));
            LocalStore.Record("b", "event", "yard", "car", 1010, null, null, 0.8, "yard", incident);
            Assert.Contains("front (person)", LocalStore.IncidentContext("b"));
        }

        [Fact]
        public void Correlate_Off_DoesNotGroup()
        {
            Program.settings.options.correlate = 0;
            LocalStore.Record("a", "event", "front", "person", 1000, null, null, 0.9, "front", null);
            Assert.Null(LocalStore.LinkIncident("b", "yard", 1001));
        }

        [Fact]
        public void TrackSent_AppendsRelatedCamerasToCaption()
        {
            Program.settings.options.correlate = 60;
            string first = Program.TrackSent("event", "n1", "front", "hello", Array.Empty<string>(), "person", 2000, null, null, 0.9);
            Assert.Equal("hello", first);
            string second = Program.TrackSent("event", "n2", "yard", "hi", Array.Empty<string>(), "car", 2010, null, null, 0.8);
            Assert.Contains("front", second);
            Assert.Contains(L10n.Tg.T("caption.related"), second);
        }

        [Fact]
        public void StatsSearch_FindsAiTextFromLocalDb()
        {
            TestEnv.AddEvent(frigate, "vis-1", "front", "person", 0.9, TestEnv.Now(-30));
            LocalStore.Record("vis-1", "event", "front", "person", TestEnv.Now(-30), null, null, 0.9, "cap", null);
            LocalStore.SetAi("vis-1", "wearing a yellow hat");

            Assert.Equal("vis-1", Assert.Single(StatsService.Search("yellow", null, null, null, null, 10, out int total)).id);
            Assert.Equal(1, total);
            var overlay = Assert.Single(StatsService.Search("vis-1", null, null, null, null, 10, out _));
            Assert.Contains("yellow", overlay.ai_text);
        }

        [Fact]
        public void StatsSearch_JoinsReviewAiOntoTheDetectionEvent()
        {
            double t = TestEnv.Now(-40);
            TestEnv.AddEvent(frigate, "det-1", "front", "person", 0.73, t, t + 5);
            TestEnv.AddReview(frigate, "rev-1", "front", "alert", t + 7, "{\"detections\":[\"det-1\"],\"objects\":[\"person\"]}");
            LocalStore.Record("rev-1", "review", "front", "person", t + 7, t + 10, null, 0, "cap", null);
            LocalStore.SetAi("rev-1", "человек идёт по дорожке");

            var rows = StatsService.Search("front", null, null, null, null, 20, out int total);
            Assert.DoesNotContain(rows, r => r.id == "rev-1");
            var ev = Assert.Single(rows, r => r.id == "det-1");
            Assert.Equal(0.73, ev.score, 3);
            Assert.Contains("дорожке", ev.ai_text);
            Assert.True(total >= 1);
        }

        [Fact]
        public void StatsSearch_ByAiText_ReturnsTheFrigateEventNotTheReview()
        {
            double t = TestEnv.Now(-20);
            TestEnv.AddEvent(frigate, "det-2", "yard", "person", 0.8, t, t + 3);
            TestEnv.AddReview(frigate, "rev-2", "yard", "alert", t, "{\"detections\":[\"det-2\"]}");
            LocalStore.Record("rev-2", "review", "yard", "person", t, null, null, 0, "cap", null);
            LocalStore.SetAi("rev-2", "wearing a blue jacket");

            var rows = StatsService.Search("blue jacket", null, null, null, null, 10, out _);
            var ev = Assert.Single(rows);
            Assert.Equal("det-2", ev.id);
            Assert.Equal(0.8, ev.score, 3);
            Assert.Contains("blue jacket", ev.ai_text);
        }

        [Fact]
        public void GetLast_OverlaysReviewAiOnTheEvent()
        {
            double t = TestEnv.Now(-15);
            TestEnv.AddEvent(frigate, "det-3", "front", "person", 0.9, t, t + 2);
            TestEnv.AddReview(frigate, "rev-3", "front", "alert", t, "{\"detections\":[\"det-3\"]}");
            LocalStore.Record("rev-3", "review", "front", "person", t, null, null, 0, "cap", null);
            LocalStore.SetAi("rev-3", "a person at the gate");

            var ev = Assert.Single(StatsService.GetLast("front", null, 10), r => r.id == "det-3");
            Assert.Contains("gate", ev.ai_text);
        }

        [Fact]
        public void ResolveEvent_MapsAReviewIdToItsDetection()
        {
            double t = TestEnv.Now(-10);
            TestEnv.AddEvent(frigate, "det-4", "front", "car", 0.6, t);
            TestEnv.AddReview(frigate, "rev-4", "front", "alert", t, "{\"detections\":[\"det-4\"]}");
            Assert.Equal("det-4", StatsService.ResolveEvent("rev-4").id);
            Assert.Equal("det-4", StatsService.ResolveEvent("det-4").id);
            Assert.Null(StatsService.ResolveEvent("missing"));
        }
    }
}
