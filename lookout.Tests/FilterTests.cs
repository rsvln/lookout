using Xunit;

namespace Lookout.Tests
{
    [Collection(ProgramStateCollection.Name)]
    public class FilterTests : IDisposable
    {
        readonly string db;

        public FilterTests()
        {
            db = TestEnv.CreateFrigateDb();
            TestEnv.Init(db);
        }

        public void Dispose() => TestEnv.DeleteDb(db);

        static Camera Cam(params (string label, int percent)[] objects) => new Camera
        {
            camera = "front",
            objects = objects.Select(o => new Objects { label = o.label, percent = o.percent }).ToList()
        };

        [Fact]
        public void ObjectPasses_NoList_PassesEverything()
        {
            Assert.True(Program.ObjectPasses(Cam(), "person", 0.01));
            Assert.True(Program.ObjectPasses(new Camera { objects = null }, "car", 0));
        }

        [Fact]
        public void ObjectPasses_UnlistedLabel_Fails()
        {
            Assert.False(Program.ObjectPasses(Cam(("person", 50)), "dog", 0.99));
        }

        [Theory]
        [InlineData(0.49, false)]
        [InlineData(0.50, true)]
        [InlineData(0.80, true)]
        public void ObjectPasses_ChecksPercentThreshold(double score, bool expected)
        {
            Assert.Equal(expected, Program.ObjectPasses(Cam(("person", 50)), "person", score));
        }

        [Fact]
        public void EventPasses_UsesTheBestScoreSoFar()
        {
            var cam = Cam(("person", 70));
            Assert.True(Program.EventPasses(cam, new BeforeAfterFE { id = "e1", camera = "front", label = "person", top_score = 0.9, score = 0.2 }));
            Assert.True(Program.EventPasses(cam, new BeforeAfterFE { id = "e2", camera = "front", label = "person", top_score = 0, score = 0.75 }));
            Assert.False(Program.EventPasses(cam, new BeforeAfterFE { id = "e3", camera = "front", label = "person", top_score = 0.3, score = 0.4 }));
        }

        [Fact]
        public void EventPasses_WrongLabel_Fails()
        {
            Assert.False(Program.EventPasses(Cam(("person", 10)), new BeforeAfterFE { id = "e", camera = "front", label = "car", top_score = 1 }));
        }

        static FrigateReview Review(string id, List<string> objects, params string[] detections) => new FrigateReview
        {
            type = "end",
            after = new AfterBeforeReview
            {
                id = id,
                camera = "front",
                data = new DataReview { objects = objects, detections = detections.ToList() }
            }
        };

        [Fact]
        public void ReviewPasses_NoObjectFilter_Passes()
        {
            Assert.True(Program.ReviewPasses(Cam(), Review("r", new List<string> { "car" })));
        }

        [Fact]
        public void ReviewPasses_NoMatchingLabel_Fails()
        {
            Assert.False(Program.ReviewPasses(Cam(("person", 50)), Review("r", new List<string> { "car" })));
            Assert.False(Program.ReviewPasses(Cam(("person", 50)), Review("r", null)));
        }

        [Fact]
        public void ReviewPasses_DetectionNotInDbYet_IsJudgedByLabelOnly()
        {
            Assert.True(Program.ReviewPasses(Cam(("person", 90)), Review("r", new List<string> { "person" }, "missing-event")));
        }

        [Fact]
        public void ReviewPasses_ChecksScoresOfDetectionsInDb()
        {
            double now = TestEnv.Now();
            TestEnv.AddEvent(db, "low", "front", "person", 0.4, now - 10, now - 5);
            TestEnv.AddEvent(db, "high", "front", "person", 0.8, now - 10, now - 5);
            var cam = Cam(("person", 60));

            Assert.False(Program.ReviewPasses(cam, Review("r1", new List<string> { "person" }, "low")));
            Assert.True(Program.ReviewPasses(cam, Review("r2", new List<string> { "person" }, "high")));
            // One passing detection is enough for the whole review.
            Assert.True(Program.ReviewPasses(cam, Review("r3", new List<string> { "person" }, "low", "high")));
        }
    }
}
