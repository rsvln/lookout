using Newtonsoft.Json;

namespace Lookout
{
    internal partial class Program
    {
        // What the retry queue does with a stored job: runs the worker (or hands the task to its queue) again.
        static void RegisterRetryHandlers()
        {
            RetryQueue.Register("event-new", job => FrigateEventNewWorker(JsonConvert.DeserializeObject<FrigateEvent>(job.Payload), job.Attempt));
            RetryQueue.Register("event-end", job => FrigateEventEndWorker(JsonConvert.DeserializeObject<FrigateEvent>(job.Payload), job.Attempt));
            RetryQueue.Register("review-new", job => FrigateReviewNewWorker(JsonConvert.DeserializeObject<FrigateReview>(job.Payload), job.Attempt));
            RetryQueue.Register("review-end", job => FrigateReviewEndWorker(JsonConvert.DeserializeObject<FrigateReview>(job.Payload), job.Attempt));

            RetryQueue.Register("ai", job =>
            {
                var task = JsonConvert.DeserializeObject<AITask>(job.Payload);
                task.RetryAttempt = job.Attempt;
                if (aiQueue == null || !goAI)
                    Log("retry", job.EventId, job.Camera, "AI service is not configured any more, dropped");
                else
                    aiQueue.AddToQueue(task);
                return Task.CompletedTask;
            });

            RetryQueue.Register("fr", job =>
            {
                var task = JsonConvert.DeserializeObject<FRTask>(job.Payload);
                task.RetryAttempt = job.Attempt;
                if (frQueue == null || !goFR)
                    Log("retry", job.EventId, job.Camera, "Face recognition is not configured any more, dropped");
                else
                    frQueue.AddToQueue(task);
                return Task.CompletedTask;
            });
        }
    }
}
