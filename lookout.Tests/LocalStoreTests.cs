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
    }
}
