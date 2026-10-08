using System.Net.Http;
using Xunit;

namespace Lookout.Tests
{
    [Collection(ProgramStateCollection.Name)]
    public class RetryQueueTests : IDisposable
    {
        readonly string dir;

        public RetryQueueTests()
        {
            TestEnv.Init();
            Program.settings.options.retrymax = 3;
            Program.settings.options.retrybackoff = 10;
            dir = Path.Combine(Path.GetTempPath(), "lookout-retry-" + Guid.NewGuid().ToString("N"));
            RetryQueue.Dir = dir;
            Metrics.Reset();
        }

        public void Dispose()
        {
            RetryQueue.Dir = RetryQueue.DefaultDir;
            try { Directory.Delete(dir, true); } catch { }
        }

        static readonly Exception Down = new HttpRequestException("connection refused");

        [Theory]
        [InlineData(1, 10)]
        [InlineData(2, 20)]
        [InlineData(3, 40)]
        [InlineData(4, 80)]
        public void Backoff_DoublesEachAttempt(int attempt, int seconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), RetryQueue.Backoff(attempt, 10));
        }

        [Fact]
        public void Backoff_IsCappedAtAnHour()
        {
            Assert.Equal(TimeSpan.FromHours(1), RetryQueue.Backoff(30, 30));
        }

        [Fact]
        public void IsTransient_RecognizesNetworkErrorsOnly()
        {
            Assert.True(RetryQueue.IsTransient(new HttpRequestException("x")));
            Assert.True(RetryQueue.IsTransient(new TimeoutException()));
            Assert.True(RetryQueue.IsTransient(new TaskCanceledException()));
            Assert.True(RetryQueue.IsTransient(new Exception("wrapped", new HttpRequestException("inner"))));
            Assert.True(RetryQueue.IsTransient(new Telegram.Bot.Exceptions.ApiRequestException("bad gateway", 502)));
            Assert.False(RetryQueue.IsTransient(new Telegram.Bot.Exceptions.ApiRequestException("forbidden", 403)));
            Assert.False(RetryQueue.IsTransient(new InvalidOperationException("bug")));
            Assert.False(RetryQueue.IsTransient(new NullReferenceException()));
        }

        [Fact]
        public void TrySchedule_IsOffByDefault()
        {
            Program.settings.options.retrymax = 0;
            Assert.False(RetryQueue.Enabled);
            Assert.False(RetryQueue.TrySchedule("ai", "{}", 0, "e1", "front", Down));
            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public void TrySchedule_WritesAJobFile()
        {
            var before = DateTime.UtcNow;
            Assert.True(RetryQueue.TrySchedule("review-end", "{\"a\":1}", 0, "e1", "front", Down));

            var job = Assert.Single(RetryQueue.List());
            Assert.Equal("review-end", job.Kind);
            Assert.Equal("{\"a\":1}", job.Payload);
            Assert.Equal(1, job.Attempt);
            Assert.Equal("e1", job.EventId);
            Assert.Equal("front", job.Camera);
            Assert.Equal("connection refused", job.Error);
            Assert.InRange(job.NextAttemptUtc, before.AddSeconds(9), before.AddSeconds(15));
            Assert.Equal(1, RetryQueue.Count());
            Assert.Equal(1, Metrics.Get("lookout_retry_scheduled_total", "kind", "review-end"));
        }

        [Fact]
        public void TrySchedule_GivesUpAfterRetryMax()
        {
            Assert.True(RetryQueue.TrySchedule("ai", "{}", 2, "e1", "front", Down));    // third and last repeat
            Assert.False(RetryQueue.TrySchedule("ai", "{}", 3, "e1", "front", Down));   // would be the fourth
            Assert.Single(RetryQueue.List());
            Assert.Equal(1, Metrics.Get("lookout_retry_given_up_total", "kind", "ai"));
        }

        [Fact]
        public void TrySchedule_SerializesObjectsToJson()
        {
            var task = new AITask { EventId = "e1", Camera = "front", ChatId = 5, MessageId = 7, ImagePaths = new List<string> { "/a.jpg" }, Prompt = "p" };
            Assert.True(RetryQueue.TrySchedule("ai", task, 0, "e1", "front", Down));

            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<AITask>(Assert.Single(RetryQueue.List()).Payload);
            Assert.Equal(7, back.MessageId);
            Assert.Equal(new[] { "/a.jpg" }, back.ImagePaths);
        }

        [Fact]
        public async Task ProcessDue_RunsOnlyDueJobsAndRemovesThem()
        {
            var calls = new List<RetryJob>();
            RetryQueue.Register("test-kind", job => { lock (calls) calls.Add(job); return Task.CompletedTask; });
            RetryQueue.TrySchedule("test-kind", "p1", 0, "e1", "front", Down);

            Assert.Empty(RetryQueue.ProcessDue(DateTime.UtcNow));               // not due yet
            Assert.Equal(1, RetryQueue.Count());

            var started = RetryQueue.ProcessDue(DateTime.UtcNow.AddMinutes(1));
            await Task.WhenAll(started);

            var run = Assert.Single(calls);
            Assert.Equal("p1", run.Payload);
            Assert.Equal(1, run.Attempt);
            Assert.Equal(0, RetryQueue.Count());
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public async Task ProcessDue_HandlerFailingWithNetworkError_IsScheduledAgain()
        {
            RetryQueue.Register("flaky", job => throw new HttpRequestException("still down"));
            RetryQueue.TrySchedule("flaky", "p", 0, "e1", "front", Down);

            await Task.WhenAll(RetryQueue.ProcessDue(DateTime.UtcNow.AddMinutes(1)));

            var job = Assert.Single(RetryQueue.List());
            Assert.Equal(2, job.Attempt);
            Assert.True(job.NextAttemptUtc > DateTime.UtcNow.AddSeconds(15));      // backoff doubled
        }

        [Fact]
        public async Task ProcessDue_HandlerFailingWithABug_IsDropped()
        {
            RetryQueue.Register("buggy", job => throw new InvalidOperationException("bug"));
            RetryQueue.TrySchedule("buggy", "p", 0, "e1", "front", Down);

            await Task.WhenAll(RetryQueue.ProcessDue(DateTime.UtcNow.AddMinutes(1)));

            Assert.Equal(0, RetryQueue.Count());
        }

        [Fact]
        public void ProcessDue_MovesUnreadableFilesAside()
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "20200101000000-ai-broken.json"), "not json {");

            Assert.Empty(RetryQueue.ProcessDue(DateTime.UtcNow));

            Assert.Empty(Directory.GetFiles(dir, "*.json"));
            Assert.Single(Directory.GetFiles(dir, "*.bad"));
        }

        [Fact]
        public async Task FailWorker_SchedulesOnlyWhenNothingWasSentYet()
        {
            // Nothing sent: a network failure schedules a retry.
            await Task.Run(() =>
            {
                RetryQueue.BeginRun();
                RetryQueue.FailWorker("event-end", new FrigateEvent { type = "end" }, 0, "e1", "front", Down);
            });
            Assert.Single(RetryQueue.List());

            // Something was already posted to Telegram: repeating would post it twice.
            await Task.Run(() =>
            {
                RetryQueue.BeginRun();
                RetryQueue.MarkSent();
                RetryQueue.FailWorker("event-end", new FrigateEvent { type = "end" }, 0, "e2", "front", Down);
            });
            Assert.Single(RetryQueue.List());

            // A bug is not repeated either.
            await Task.Run(() =>
            {
                RetryQueue.BeginRun();
                RetryQueue.FailWorker("event-end", new FrigateEvent { type = "end" }, 0, "e3", "front", new InvalidOperationException());
            });
            Assert.Single(RetryQueue.List());
        }
    }
}
