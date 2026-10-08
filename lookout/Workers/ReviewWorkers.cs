using Microsoft.Data.Sqlite;
using MQTTnet;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lookout
{
    internal partial class Program
    {
        // `attempt` is 0 for the original run and the retry number when the retry queue repeats the worker.
        async static Task FrigateReviewNewWorker(FrigateReview fr, int attempt = 0)
        {
            RetryQueue.BeginRun();
            NotifyContext.Begin(settings.frigate.cameras.Find(c => c.camera == fr.after.camera), fr.after.data?.detections?.FirstOrDefault() ?? fr.after.id);
            try
            {
                string camera = fr.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("review", fr.after.id, camera, "Start review new/update worker");

                List<string> rulabels = new List<string>();
                if (fr.after.data?.objects != null)
                    foreach (var ob in fr.after.data.objects)
                        rulabels.Add(L10n.Tg.Label(ob.ToLower()));

                if (Cam(cami).snapshot)
                {
                    var snaps = await WaitSnapshotsAsync(camera, fr.after.data.detections, "review", fr.after.id);
                    if (snaps.Count < fr.after.data.detections.Count)
                    {
                        Log("review", fr.after.id, camera, "Error: the snapshot is not ready");
                        return;
                    }

                    string tgcaption = L10n.Tg.T("caption.review") + " \t" + fr.after.id + "\n" +
                                       L10n.Tg.T("caption.camera") + " " + fr.after.camera + "\n" +
                                       L10n.Tg.T("caption.objects") + " " + string.Join(", ", rulabels) + "\n" +
                                       L10n.Tg.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                       (fr.after.end_time.HasValue ? L10n.Tg.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                       L10n.Tg.T("caption.events") + " " + string.Join(", ", fr.after.data.detections);
                    var imagePaths = fr.after.data.detections.Select(ev => snaps[ev]).ToList();
                    tgcaption = TrackSent("review", fr.after.id, camera, tgcaption, imagePaths,
                        fr.after.data.objects == null ? null : string.Join(",", fr.after.data.objects),
                        fr.after.start_time, fr.after.end_time, fr.after.data.zones, 0);

                    List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();
                    int i = 1;
                    foreach (var ev in fr.after.data.detections)
                    {
                        md.Add(new InputMediaPhoto(
                            new InputFileStream(System.IO.File.OpenRead(snaps[ev]), fr.after.camera + "-" + ev + ".jpg"))
                        {
                            Caption = (i == 1) ? tgcaption : null,
                            ParseMode = ParseMode.Markdown
                        });
                        if (i == 9) break;
                        i++;
                    }

                    if (md.Count > 0)
                    {
                        string aiPrompt = AiPrompt(fr.after.data.objects.Contains("person"));

                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md, disableNotification: NotifySilent), "review", fr.after.id, camera);
                            await Task.Delay(100);
                            int firstmessageid = msgs[0].MessageId;
                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            x++;
                            Log("review", fr.after.id, camera, "The snapshot was sent to telegram chat " + chid);

                            if (goFR && Cam(cami).fr)
                                frQueue.AddToQueue(new FRTask
                                {
                                    ImagePaths = imagePaths,
                                    AIPrompt = aiPrompt,
                                    ChatId = long.Parse(chid),
                                    MessageId = firstmessageid,
                                    Camera = camera,
                                    EventId = fr.after.id,
                                    OriginalCaption = tgcaption
                                });
                            else if (goAI && Cam(cami).ai)
                                aiQueue.AddToQueue(new AITask
                                {
                                    ImagePaths = imagePaths,
                                    Prompt = aiPrompt,
                                    ChatId = long.Parse(chid),
                                    MessageId = firstmessageid,
                                    Camera = camera,
                                    EventId = fr.after.id,
                                    OriginalCaption = tgcaption
                                });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("review", fr.after.id, fr.after.camera, "Error while review new/update worker: " + ex.Message);
                Metrics.Inc("lookout_worker_errors_total", "worker", "review-new");
                RetryQueue.FailWorker("review-new", fr, attempt, fr.after.id, fr.after.camera, ex);
            }
        }

        private static async Task SendReviewMediaAsync(
                                        FrigateReview fr,
                                        string camera,
                                        int cami,
                                        List<string> rulabels,
                                        List<IAlbumInputMedia> md,
                                        Dictionary<string, int> firstmessages,
                                        bool firstmessage,
                                        string tgcaption,
                                        List<DbRow> dl,
                                        Dictionary<string, string> snaps)
        {
            var parts = BuildFfmpegParts(fr.after.id, camera, dl);
            int partid = parts.Count;

            if (string.IsNullOrEmpty(tgcaption))
                tgcaption = L10n.Tg.T("caption.review") + " \t" + fr.after.id + "\n" +
                            L10n.Tg.T("caption.camera") + " " + fr.after.camera + "\n" +
                            L10n.Tg.T("caption.objects") + " " + string.Join(", ", rulabels) + "\n" +
                            L10n.Tg.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                            (fr.after.end_time.HasValue ? L10n.Tg.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                            L10n.Tg.T("caption.events") + " " + string.Join(", ", fr.after.data.detections);

            // Snapshots resolved by the caller (includes current frames of events still in progress), otherwise Frigate's files.
            var imagePaths = fr.after.data.detections
                .Select(ev => snaps.TryGetValue(ev, out var p) ? p : settings.frigate.clipspath + "/" + fr.after.camera + "-" + ev + ".jpg")
                .Where(p => System.IO.File.Exists(p))
                .ToList();
            string aiPrompt = AiPrompt(fr.after.data.objects.Contains("person"));

            if (Cam(cami).gif)
            {
                string gifPath = RunFfmpegGif(parts[0].path, settings.options.gifwidth);
                if (System.IO.File.Exists(gifPath))
                {
                    try
                    {
                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            await TgCall(() => bot.SendAnimation(
                                chatId: chid, disableNotification: NotifySilent,
                                animation: InputFile.FromStream(System.IO.File.OpenRead(gifPath), Path.GetFileName(gifPath)),
                                caption: tgcaption,
                                replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                "review", fr.after.id, camera);
                            await Task.Delay(100);
                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            x++;
                            Log("review", fr.after.id, camera, "The gif was sent to telegram chat " + chid);
                        }
                    }
                    finally
                    {
                        System.IO.File.Delete(gifPath);
                    }
                }
            }

            if (Cam(cami).clip)
            {
                await Task.Delay(settings.options.retry * 100);

                if (Cam(cami).sctogether && Cam(cami).snapshot)
                {
                    for (int i = 1; i <= partid; i++)
                    {
                        if (!firstmessage)
                        {
                            md.Add(new InputMediaVideo(
                                new InputFileStream(System.IO.File.OpenRead(parts[i - 1].path),
                                fr.after.id + ((partid == 1) ? "" : "-part" + i) + ".mp4")));

                            if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                            {
                                int x = 1;
                                foreach (var chid in settings.telegram.chatids)
                                {
                                    if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                    Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md, disableNotification: NotifySilent), "review", fr.after.id, camera);
                                    await Task.Delay(100);
                                    firstmessages[chid] = msgs[0].MessageId;
                                    Log("review", fr.after.id, camera, "The snapshot and clip were sent to telegram chat " + chid);
                                    x++;

                                    if (goFR && Cam(cami).fr)
                                        frQueue.AddToQueue(new FRTask
                                        {
                                            ImagePaths = imagePaths,
                                            AIPrompt = aiPrompt,
                                            ChatId = long.Parse(chid),
                                            MessageId = firstmessages[chid],
                                            Camera = camera,
                                            EventId = fr.after.id,
                                            OriginalCaption = tgcaption
                                        });
                                    else if (goAI && Cam(cami).ai)
                                        aiQueue.AddToQueue(new AITask
                                        {
                                            ImagePaths = imagePaths,
                                            Prompt = aiPrompt,
                                            ChatId = long.Parse(chid),
                                            MessageId = firstmessages[chid],
                                            Camera = camera,
                                            EventId = fr.after.id,
                                            OriginalCaption = tgcaption
                                        });
                                }
                            }
                        }
                        else
                        {
                            string cap = fr.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.Tg.T("caption.video");
                            int x = 1;
                            foreach (var chid in settings.telegram.chatids)
                            {
                                await TgCall(() => bot.SendVideo(
                                    chatId: chid, disableNotification: NotifySilent,
                                    video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                    caption: cap,
                                    supportsStreaming: true,
                                    parseMode: ParseMode.Markdown,
                                    replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                    "review", fr.after.id, camera);
                                await Task.Delay(100);
                                if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                x++;
                                Log("review", fr.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i + " ") + "was sent to telegram chat " + chid);
                            }
                        }

                        System.IO.File.Delete(parts[i - 1].path);
                    }
                }
                else
                {
                    for (int i = 1; i <= partid; i++)
                    {
                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            string cap = (firstmessages[chid] != -1)
                                ? fr.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.Tg.T("caption.video")
                                : fr.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.Tg.T("caption.video") + "\n" +
                                  L10n.Tg.T("caption.camera") + " " + fr.after.camera + "\n" +
                                  L10n.Tg.T("caption.objects") + " " + string.Join(", ", rulabels) + "\n" +
                                  L10n.Tg.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                  (fr.after.end_time.HasValue ? L10n.Tg.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                  L10n.Tg.T("caption.events") + " " + string.Join(", ", fr.after.data.detections);

                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            await TgCall(() => bot.SendVideo(
                                chatId: chid, disableNotification: NotifySilent,
                                video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                caption: cap,
                                supportsStreaming: true,
                                parseMode: ParseMode.Markdown,
                                replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                "review", fr.after.id, camera);
                            await Task.Delay(100);
                            Log("review", fr.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i + " ") + "was sent to telegram chat " + chid);
                            x++;
                        }

                        System.IO.File.Delete(parts[i - 1].path);
                    }
                }
            }
            else
            {
                foreach (var part in parts)
                    System.IO.File.Delete(part.path);
            }
        }

        async static Task FrigateReviewEndWorker(FrigateReview fr, int attempt = 0)
        {
            RetryQueue.BeginRun();
            NotifyContext.Begin(settings.frigate.cameras.Find(c => c.camera == fr.after.camera), fr.after.data?.detections?.FirstOrDefault() ?? fr.after.id);
            try
            {
                string camera = fr.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("review", fr.after.id, camera, "Start review end worker");

                bool firstmessage = false;
                string tgcaption = "";
                Dictionary<string, int> firstmessages = new Dictionary<string, int>();
                foreach (var chid in settings.telegram.chatids)
                    firstmessages.Add(chid, -1);

                List<string> rulabels = new List<string>();
                if (fr.after.data?.objects != null)
                    foreach (var ob in fr.after.data.objects)
                        rulabels.Add(L10n.Tg.Label(ob.ToLower()));

                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();
                var snaps = new Dictionary<string, string>();

                if (Cam(cami).snapshot && Cam(cami).snapshottrigger == "end")
                {
                    snaps = await WaitSnapshotsAsync(camera, fr.after.data.detections, "review", fr.after.id);
                    if (snaps.Count < fr.after.data.detections.Count)
                        Log("review", fr.after.id, camera, "Some snapshots not ready after timeout, will skip missing");

                    tgcaption = L10n.Tg.T("caption.review") + " \t" + fr.after.id + "\n" +
                                L10n.Tg.T("caption.camera") + " " + fr.after.camera + "\n" +
                                L10n.Tg.T("caption.objects") + " " + string.Join(", ", rulabels) + "\n" +
                                L10n.Tg.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                (fr.after.end_time.HasValue ? L10n.Tg.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                L10n.Tg.T("caption.events") + " " + string.Join(", ", fr.after.data.detections);
                    tgcaption = TrackSent("review", fr.after.id, camera, tgcaption, snaps.Values,
                        fr.after.data.objects == null ? null : string.Join(",", fr.after.data.objects),
                        fr.after.start_time, fr.after.end_time, fr.after.data.zones, 0);

                    int i = 1;
                    foreach (var ev in fr.after.data.detections)
                    {
                        if (!snaps.TryGetValue(ev, out string snapPath))
                        {
                            Log("review", fr.after.id, camera, "Snapshot not found, skipping: " + ev);
                            continue;
                        }
                        md.Add(new InputMediaPhoto(
                            new InputFileStream(System.IO.File.OpenRead(snapPath), fr.after.camera + "-" + ev + ".jpg"))
                        {
                            Caption = (i == 1) ? tgcaption : null,
                            ParseMode = ParseMode.Markdown
                        });
                        if (i == settings.telegram.mediagrouplimit - 1) break;
                        i++;
                    }
                    if ((md.Count > 0) && (!Cam(cami).sctogether || !Cam(cami).clip))
                    {
                        firstmessage = true;
                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md, disableNotification: NotifySilent), "review", fr.after.id, camera);
                            await Task.Delay(100);
                            firstmessages[chid] = msgs[0].MessageId;
                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            x++;
                            Log("review", fr.after.id, camera, "The snapshot was sent to telegram chat " + chid);
                        }

                        if (goFR && Cam(cami).fr)
                            foreach (var chid in settings.telegram.chatids)
                                frQueue.AddToQueue(new FRTask
                                {
                                    ImagePaths = fr.after.data.detections.Where(snaps.ContainsKey).Select(ev => snaps[ev]).ToList(),
                                    AIPrompt = AiPrompt(fr.after.data.objects.Contains("person")),
                                    ChatId = long.Parse(chid),
                                    MessageId = firstmessages[chid],
                                    Camera = camera,
                                    EventId = fr.after.id,
                                    OriginalCaption = tgcaption
                                });
                        else if (goAI && Cam(cami).ai)
                            foreach (var chid in settings.telegram.chatids)
                                aiQueue.AddToQueue(new AITask
                                {
                                    ImagePaths = fr.after.data.detections.Where(snaps.ContainsKey).Select(ev => snaps[ev]).ToList(),
                                    Prompt = AiPrompt(fr.after.data.objects.Contains("person")),
                                    ChatId = long.Parse(chid),
                                    MessageId = firstmessages[chid],
                                    Camera = camera,
                                    EventId = fr.after.id,
                                    OriginalCaption = tgcaption
                                });
                    }
                }

                if (Cam(cami).clip || Cam(cami).gif)
                {
                    int secs = 0;
                    var sqlq = (sql: new Queries().getEventQuery("review", true), id: fr.after.id, camera: fr.after.camera);
                    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                    bool isSuccess = false;
                    var waitSw = Stopwatch.StartNew();

                    while (secs <= settings.options.timeout)
                    {
                        using var db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                        db.Open();
                        using var dr = RecordingsCommand(db, sqlq).ExecuteReader();
                        if (dr.HasRows)
                        {
                            isSuccess = true;
                            Metrics.Observe(waitSw.Elapsed.TotalSeconds);
                            Log("review", fr.after.id, camera, "All recordings are ready");

                            if (Cam(cami).trueend)
                            {
                                var fes = new FrigateReview { type = "trueend", before = fr.before, after = fr.after };
                                Log("review", fr.after.id, camera, "Sending the trueend review");
                                await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                    .WithTopic(settings.mqtt.reviewstopic)
                                    .WithPayload(JsonConvert.SerializeObject(fes))
                                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                                    .WithRetainFlag()
                                    .Build(), CancellationToken.None);
                            }

                            List<DbRow> dl = new List<DbRow>();
                            while (dr.Read())
                                dl.Add(new DbRow
                                {
                                    path = (string)dr["path"],
                                    start_time = (double)dr["start_time"],
                                    end_time = (double)dr["end_time"],
                                    duration = (double)dr["duration"],
                                    realpath = dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath),
                                    size = (new FileInfo(dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath))).Length
                                });
                            db.Close();
                            await Task.Delay(10);

                            await SendReviewMediaAsync(fr, camera, cami, rulabels, md, firstmessages, firstmessage, tgcaption, dl, snaps);
                            break;
                        }

                        secs += settings.options.retry;
                        await Task.Delay(settings.options.retry * 1000);
                    }

                    if (!isSuccess)
                    {
                        Metrics.Observe(waitSw.Elapsed.TotalSeconds);
                        if (settings.options.sendeverythingwhatyouhave)
                        {
                            Log("review", fr.after.id, camera, "Timeout expired, video files were not ready. Trying to send everything the frigate has");
                            using var db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                            sqlq = (new Queries().getEventQuery("review", false), fr.after.id, fr.after.camera);
                            db.Open();
                            using var dr = RecordingsCommand(db, sqlq).ExecuteReader();
                            if (dr.HasRows)
                            {
                                Log("review", fr.after.id, camera, "All recordings are ready");

                                if (Cam(cami).trueend)
                                {
                                    var fes = new FrigateReview { type = "trueend", before = fr.before, after = fr.after };
                                    Log("review", fr.after.id, camera, "Sending the trueend review");
                                    await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                        .WithTopic(settings.mqtt.reviewstopic)
                                        .WithPayload(JsonConvert.SerializeObject(fes))
                                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                                        .WithRetainFlag()
                                        .Build(), CancellationToken.None);
                                }

                                List<DbRow> dl = new List<DbRow>();
                                while (dr.Read())
                                    dl.Add(new DbRow
                                    {
                                        path = (string)dr["path"],
                                        start_time = (double)dr["start_time"],
                                        end_time = (double)dr["end_time"],
                                        duration = (double)dr["duration"],
                                        realpath = dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath),
                                        size = (new FileInfo(dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath))).Length
                                    });
                                db.Close();
                                await Task.Delay(10);

                                await SendReviewMediaAsync(fr, camera, cami, rulabels, md, firstmessages, firstmessage, tgcaption, dl, snaps);
                            }
                        }
                        else
                            Log("review", fr.after.id, camera, "Timeout ended, video files were not ready");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("review", fr.after.id, fr.after.camera, "Error in review end worker: " + ex.Message);
                Metrics.Inc("lookout_worker_errors_total", "worker", "review-end");
                RetryQueue.FailWorker("review-end", fr, attempt, fr.after.id, fr.after.camera, ex);
            }
        }



    }
}
