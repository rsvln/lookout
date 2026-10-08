using Xunit;

namespace Lookout.Tests
{
    [Collection(ProgramStateCollection.Name)]
    public class MetricsAndHealthTests : IDisposable
    {
        readonly string db;

        public MetricsAndHealthTests()
        {
            db = TestEnv.CreateFrigateDb();
            TestEnv.Init(db);
            Metrics.Reset();
            Health.ResetCache();
        }

        public void Dispose() => TestEnv.DeleteDb(db);

        [Fact]
        public void Counters_AreRenderedWithLabels()
        {
            Metrics.Inc("lookout_events_total", "source", "review", "type", "end");
            Metrics.Inc("lookout_events_total", "source", "review", "type", "end");
            Metrics.Inc("lookout_events_total", "source", "event", "type", "new");
            Metrics.Inc("lookout_telegram_errors_total");

            string text = Metrics.Render();

            Assert.Contains("# TYPE lookout_events_total counter", text);
            Assert.Contains("lookout_events_total{source=\"review\",type=\"end\"} 2", text);
            Assert.Contains("lookout_events_total{source=\"event\",type=\"new\"} 1", text);
            Assert.Contains("\nlookout_telegram_errors_total 1\n", text);
        }

        [Fact]
        public void LabelValues_AreEscaped()
        {
            Metrics.Inc("lookout_x_total", "k", "a\"b\\c");
            Assert.Contains("lookout_x_total{k=\"a\\\"b\\\\c\"} 1", Metrics.Render());
        }

        [Fact]
        public void Histogram_HasCumulativeBucketsSumAndCount()
        {
            Metrics.Observe(0.5);
            Metrics.Observe(7);
            Metrics.Observe(5000);

            string text = Metrics.Render();

            Assert.Contains("# TYPE lookout_trueend_wait_seconds histogram", text);
            Assert.Contains("lookout_trueend_wait_seconds_bucket{le=\"1\"} 1", text);
            Assert.Contains("lookout_trueend_wait_seconds_bucket{le=\"10\"} 2", text);
            Assert.Contains("lookout_trueend_wait_seconds_bucket{le=\"1200\"} 2", text);
            Assert.Contains("lookout_trueend_wait_seconds_bucket{le=\"+Inf\"} 3", text);
            Assert.Contains("lookout_trueend_wait_seconds_sum 5007.5", text);
            Assert.Contains("lookout_trueend_wait_seconds_count 3", text);
        }

        [Fact]
        public void Gauges_AreAlwaysThere()
        {
            string text = Metrics.Render();
            Assert.Contains("lookout_ai_queue_length 0", text);
            Assert.Contains("lookout_fr_queue_length 0", text);
            Assert.Contains("lookout_retry_queue_length ", text);
            Assert.Contains("lookout_mqtt_connected 0", text);
            Assert.Contains("lookout_info{version=\"", text);
        }

        [Fact]
        public async Task Health_ReportsEveryCheck_AndFailsWithoutMqtt()
        {
            var report = await Health.CheckAsync();

            Assert.Equal(new[] { "db", "ffmpeg", "frigate", "mqtt" }, report.checks.Keys.OrderBy(k => k));
            Assert.True(report.checks["db"].ok);
            Assert.False(report.checks["mqtt"].ok);       // the tests never connect to a broker
            Assert.False(report.Healthy);
            Assert.Equal("unhealthy", report.status);
        }

        [Fact]
        public async Task Health_MissingDatabase_IsReported()
        {
            Program.settings.frigate.dbpath = Path.Combine(Path.GetTempPath(), "does-not-exist.db");

            var report = await Health.CheckAsync();

            Assert.False(report.checks["db"].ok);
            Assert.Contains("not found", report.checks["db"].detail);
        }
    }
}
