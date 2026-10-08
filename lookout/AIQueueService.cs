using System.Collections.Concurrent;
using Telegram.Bot;

namespace Lookout
{
    public class AIQueueService
    {
        private readonly ConcurrentQueue<AITask> queue = new ConcurrentQueue<AITask>();
        private readonly HttpClient httpClient;
        private readonly ITelegramBotClient tgBotClient;
        private readonly SemaphoreSlim semaphore;
        private readonly CancellationTokenSource cts;
        private readonly IAiProvider provider;
        private readonly int resizeToWidth;
        private int activeCount = 0;

        private Task workerTask;

        public AIQueueService(
            ITelegramBotClient botClient, 
            AISettings aiSettings,
            int maxConcurrentRequests = 2)
        {
            resizeToWidth = aiSettings.resizetowidth;
            tgBotClient = botClient;
            httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            provider = AiProviders.Create(aiSettings, httpClient);
            semaphore = new SemaphoreSlim(2);
            cts = new CancellationTokenSource();
        }

        public void Start()
        {
            workerTask = Task.Run(() => ProcessQueueAsync(cts.Token));
            Program.Log("app", "", "", "AI queue service started");
        }

        public void Stop()
        {
            cts.Cancel();
            workerTask?.Wait();
            Program.Log("ai", "", "", "Queue service stopped");
        }

        public void AddToQueue(AITask task)
        {
            task.QueuedAt = DateTime.Now;
            queue.Enqueue(task);
            Program.Log("ai", task.EventId, task.Camera, $"Added to queue ({queue.Count} in queue, {activeCount} active)");
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (queue.TryDequeue(out var task))
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        Interlocked.Increment(ref activeCount);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ProcessTaskAsync(task);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref activeCount);
                                semaphore.Release();
                            }
                        }, cancellationToken);
                    }
                    else
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Program.Log("ai", "", "", $"Queue processing error: {ex.Message}");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        private async Task ProcessTaskAsync(AITask task)
        {
            try
            {
                Program.Log("ai", task.EventId, task.Camera, $"Processing {task.ImagePaths.Count} images, queued {(DateTime.Now - task.QueuedAt).TotalSeconds:F1}s ago");

                var descriptions = new List<string>();
                Exception failure = null;   // set only when the retry queue is on (CallAIApiAsync rethrows then)
                int idx = 1;
                foreach (var path in task.ImagePaths)
                {
                    if (!System.IO.File.Exists(path))
                    {
                        Program.Log("ai", task.EventId, task.Camera, $"Image not found: {path}");
                        idx++;
                        continue;
                    }
                    try
                    {
                        var desc = await CallAIApiAsync(path, task.Prompt, task.EventId, task.Camera);
                        if (!string.IsNullOrEmpty(desc))
                            descriptions.Add(task.ImagePaths.Count > 1 ? $"{idx}. {desc}" : desc);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    idx++;
                }

                // Ollama is unreachable: the whole task is repeated later instead of posting a partial description.
                if (failure != null && RetryQueue.IsTransient(failure) && RetryQueue.TrySchedule("ai", task, task.RetryAttempt, task.EventId, task.Camera, failure))
                    return;

                if (descriptions.Count == 0)
                {
                    Program.Log("ai", task.EventId, task.Camera, "No AI descriptions returned");
                    return;
                }

                try
                {
                    await UpdateTelegramMessageAsync(task, string.Join("\n\n", descriptions));
                }
                catch (Exception ex) when (RetryQueue.Enabled)
                {
                    // Telegram is unreachable (the retry queue is on, so the failure was passed on): repeat the task later.
                    if (RetryQueue.IsTransient(ex) && RetryQueue.TrySchedule("ai", task, task.RetryAttempt, task.EventId, task.Camera, ex))
                        return;
                    throw;
                }
                Program.Log("ai", task.EventId, task.Camera, "Task completed");
            }
            catch (Exception ex)
            {
                Program.Log("ai", task.EventId, task.Camera, $"Task failed: {ex.Message}");
            }
        }

        // Downscales to `width` (never upscales) and returns JPEG bytes, or null if ffmpeg failed.
        private static async Task<byte[]> ResizeWithFfmpegAsync(string imagePath, int width)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", imagePath,
                                      "-vf", $"scale='min({width},iw)':-2", "-frames:v", "1", "-q:v", "3",
                                      "-f", "image2", "-c:v", "mjpeg", "pipe:1" })
                psi.ArgumentList.Add(a);

            try
            {
                using var p = System.Diagnostics.Process.Start(psi);
                using var ms = new MemoryStream();
                var stderr = p.StandardError.ReadToEndAsync();
                await p.StandardOutput.BaseStream.CopyToAsync(ms);
                await p.WaitForExitAsync();
                await stderr;
                return p.ExitCode == 0 && ms.Length > 0 ? ms.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> CallAIApiAsync(string imagePath, string prompt, string eventId, string camera)
        {
            try
            {
                var imageBytes = await System.IO.File.ReadAllBytesAsync(imagePath);

                if (resizeToWidth > 0)
                {
                    var resized = await ResizeWithFfmpegAsync(imagePath, resizeToWidth);
                    if (resized != null)
                        imageBytes = resized;
                    else
                        Program.Log("ai", eventId, camera, "Resize failed, sending original image");
                }
                
                var description = await provider.DescribeAsync(imageBytes, prompt);
                if (string.IsNullOrEmpty(description))
                    return null;

                return L10n.Tg.T("caption.ai") + " " + description;
            }
            catch (Exception ex)
            {
                Program.Log("ai", eventId, camera, $"API call failed: {ex.Message}");
                Metrics.Inc("lookout_ai_errors_total", "provider", provider.Name);
                if (provider.Name == "ollama")
                    Metrics.Inc("lookout_ollama_errors_total");
                if (RetryQueue.Enabled)
                    throw;
                return null;
            }
        }


        private async Task UpdateTelegramMessageAsync(AITask task, string description)
        {
            try
            {
                var caption = task.OriginalCaption + "\n\n" + description;
                if (caption.Length > 1024)
                    caption = caption.Substring(0, 1021) + "...";

                await tgBotClient.EditMessageCaption(
                    chatId: task.ChatId,
                    messageId: task.MessageId,
                    caption: caption
                );

                Program.Log("ai", task.EventId, task.Camera, $"Telegram message updated: {task.ChatId}/{task.MessageId}");
            }
            catch (Exception ex)
            {
                Program.Log("ai", task.EventId, task.Camera, $"Failed to update Telegram message: {ex.Message}");
                Metrics.Inc("lookout_telegram_errors_total");
                if (RetryQueue.Enabled)
                    throw;
            }
        }

        public int GetQueueSize()
        {
            return queue.Count;
        }
    }
}