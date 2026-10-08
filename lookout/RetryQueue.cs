using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Lookout
{
    public class RetryJob
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Payload { get; set; }
        // Number of this retry (1 = the first repeat of the original attempt).
        public int Attempt { get; set; }
        public DateTime NextAttemptUtc { get; set; }
        public string Camera { get; set; }
        public string EventId { get; set; }
        public string Error { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    // Jobs that failed because Telegram, Ollama or CompreFace was unreachable are kept on disk (one JSON file each) and
    // repeated with exponential backoff: options.retrybackoff seconds, doubled each time, at most an hour. After
    // options.retrymax repeats a job is dropped. retrymax = 0 (default) switches the queue off: failures are only logged.
    // Files survive a restart; jobs that were running during one are started again.
    public static class RetryQueue
    {
        public const string DefaultDir = "/var/log/lookout/retry";
        static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

        // Folder of the job files (changed only by tests).
        public static string Dir { get; set; } = DefaultDir;

        static readonly ConcurrentDictionary<string, Func<RetryJob, Task>> handlers = new ConcurrentDictionary<string, Func<RetryJob, Task>>();
        static readonly ConcurrentDictionary<string, byte> running = new ConcurrentDictionary<string, byte>();
        static readonly object fileLock = new object();
        static CancellationTokenSource cts;

        public static bool Enabled => (Program.settings?.options?.retrymax ?? 0) > 0;

        public static void Register(string kind, Func<RetryJob, Task> handler) => handlers[kind] = handler;

        public static TimeSpan Backoff(int attempt, int baseSeconds)
        {
            if (attempt < 1) attempt = 1;
            double seconds = Math.Max(1, baseSeconds) * Math.Pow(2, Math.Min(attempt - 1, 20));
            return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
        }

        // Network trouble that is worth repeating, as opposed to a bug or a rejected request.
        public static bool IsTransient(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is HttpRequestException || e is SocketException || e is TimeoutException || e is IOException && e.InnerException is SocketException)
                    return true;
                if (e is TaskCanceledException)
                    return true;
                // Telegram answered with an error: only server-side trouble and rate limits can pass by themselves.
                if (e is Telegram.Bot.Exceptions.ApiRequestException api)
                {
                    if (api.ErrorCode >= 500 || api.ErrorCode == 429)
                        return true;
                    continue;
                }
                // Telegram.Bot's wrapper for "could not reach the server".
                if (e is Telegram.Bot.Exceptions.RequestException)
                    return true;
            }
            return false;
        }

        // ---- what a worker has already sent -------------------------------------------------------------------------
        // A worker that failed after posting to Telegram is not repeated as a whole: it would post the same thing twice.

        class RunState { public bool AnySent; }
        static readonly AsyncLocal<RunState> run = new AsyncLocal<RunState>();

        public static void BeginRun() => run.Value = new RunState();

        public static void MarkSent()
        {
            var state = run.Value;
            if (state != null) state.AnySent = true;
        }

        // A worker failed with `ex`; repeats it later if that makes sense. `failedAttempt` is 0 for the original run.
        public static void FailWorker(string kind, object payload, int failedAttempt, string eventId, string camera, Exception ex)
        {
            if (!Enabled || !IsTransient(ex))
                return;
            if (run.Value?.AnySent == true)
            {
                Program.Log("retry", eventId, camera, "Not repeated: part of the notification was already sent");
                return;
            }
            TrySchedule(kind, JsonConvert.SerializeObject(payload), failedAttempt, eventId, camera, ex);
        }

        // ---- queue ------------------------------------------------------------------------------------------------------

        // Writes a job for the retry after `failedAttempt`; false when the queue is off or the attempts are used up.
        public static bool TrySchedule(string kind, object payload, int failedAttempt, string eventId, string camera, Exception ex)
        {
            if (!Enabled)
                return false;
            var opt = Program.settings.options;
            int attempt = failedAttempt + 1;
            if (attempt > opt.retrymax)
            {
                Program.Log("retry", eventId, camera, $"Giving up on {kind} after {failedAttempt} repeat(s): {ex?.Message}");
                Metrics.Inc("lookout_retry_given_up_total", "kind", kind);
                return false;
            }

            var delay = Backoff(attempt, opt.retrybackoff);
            var job = new RetryJob
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Payload = payload as string ?? JsonConvert.SerializeObject(payload),
                Attempt = attempt,
                NextAttemptUtc = DateTime.UtcNow + delay,
                Camera = camera,
                EventId = eventId,
                Error = ex?.Message,
                CreatedUtc = DateTime.UtcNow,
            };
            try
            {
                Save(job);
            }
            catch (Exception saveEx)
            {
                Program.Log("retry", eventId, camera, "Cannot write the retry job: " + saveEx.Message);
                return false;
            }
            Metrics.Inc("lookout_retry_scheduled_total", "kind", kind);
            Program.Log("retry", eventId, camera, $"{kind} failed ({ex?.Message}); repeat {attempt}/{opt.retrymax} in {(int)delay.TotalSeconds}s");
            return true;
        }

        static string PathOf(RetryJob job) => Path.Combine(Dir, job.NextAttemptUtc.ToString("yyyyMMddHHmmss") + "-" + job.Kind + "-" + job.Id + ".json");

        static void Save(RetryJob job)
        {
            lock (fileLock)
            {
                Directory.CreateDirectory(Dir);
                string path = PathOf(job);
                // Written aside and renamed, so a crash never leaves a half-written job.
                System.IO.File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(job, Formatting.Indented));
                System.IO.File.Move(path + ".tmp", path, overwrite: true);
            }
        }

        public static int Count()
        {
            try
            {
                return Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.json").Length + running.Count : running.Count;
            }
            catch { return 0; }
        }

        public static List<RetryJob> List()
        {
            var result = new List<RetryJob>();
            if (!Directory.Exists(Dir)) return result;
            foreach (var file in Directory.GetFiles(Dir, "*.json").OrderBy(f => f))
            {
                try { result.Add(JsonConvert.DeserializeObject<RetryJob>(System.IO.File.ReadAllText(file))); }
                catch { }
            }
            return result;
        }

        // Starts every job that is due. Returns the tasks of the jobs started, for tests to wait on; the timer ignores them.
        public static List<Task> ProcessDue(DateTime nowUtc)
        {
            var started = new List<Task>();
            if (!Directory.Exists(Dir))
                return started;

            foreach (var file in Directory.GetFiles(Dir, "*.json").OrderBy(f => f))
            {
                RetryJob job;
                try { job = JsonConvert.DeserializeObject<RetryJob>(System.IO.File.ReadAllText(file)); }
                catch (Exception ex)
                {
                    Program.Log("retry", "", "", "Unreadable retry file " + Path.GetFileName(file) + ": " + ex.Message);
                    try { System.IO.File.Move(file, file + ".bad", overwrite: true); } catch { }
                    continue;
                }
                if (job == null || job.NextAttemptUtc > nowUtc)
                    continue;

                // Claim the file: only the one that renamed it runs the job.
                string claimed = file + ".running";
                try { System.IO.File.Move(file, claimed); }
                catch { continue; }

                running[job.Id] = 0;
                started.Add(Task.Run(() => RunJobAsync(job, claimed)));
            }
            return started;
        }

        static async Task RunJobAsync(RetryJob job, string claimedFile)
        {
            try
            {
                if (!handlers.TryGetValue(job.Kind, out var handler))
                {
                    Program.Log("retry", job.EventId, job.Camera, "No handler for job kind " + job.Kind + ", dropped");
                    return;
                }
                Program.Log("retry", job.EventId, job.Camera, $"Repeating {job.Kind}, attempt {job.Attempt}");
                await handler(job);
            }
            catch (Exception ex)
            {
                Program.Log("retry", job.EventId, job.Camera, $"Repeated {job.Kind} failed: {ex.Message}");
                if (IsTransient(ex))
                    TrySchedule(job.Kind, job.Payload, job.Attempt, job.EventId, job.Camera, ex);
            }
            finally
            {
                running.TryRemove(job.Id, out _);
                try { System.IO.File.Delete(claimedFile); } catch { }
            }
        }

        // Jobs that were running when the app stopped are put back.
        static void RecoverInterrupted()
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var file in Directory.GetFiles(Dir, "*.running"))
            {
                try { System.IO.File.Move(file, file.Substring(0, file.Length - ".running".Length), overwrite: true); } catch { }
            }
        }

        public static void Start()
        {
            if (cts != null) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            try { RecoverInterrupted(); } catch { }
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (Enabled) ProcessDue(DateTime.UtcNow);
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Program.Log("retry", "", "", "Queue error: " + ex.Message); }
                }
            });
        }

        public static void Stop()
        {
            cts?.Cancel();
            cts = null;
        }
    }
}
