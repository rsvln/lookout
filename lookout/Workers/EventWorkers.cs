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
        async static Task FrigateEventNewWorker(FrigateEvent fe, int attempt = 0)
        {
            RetryQueue.BeginRun();
            NotifyContext.Begin(settings.frigate.cameras.Find(c => c.camera == fe.after.camera), fe.after.id);
            try
            {
                string camera = fe.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("event", fe.after.id, camera, "Start event new/update worker");

                string rulabel = L10n.Tg.Label(fe.after.label.ToLower())
                               + " (" + (fe.after.score * 100).ToString("0.00") + "%)";

                if (fe.after.has_snapshot && Cam(cami).snapshot)
                {
                    var snaps = await WaitSnapshotsAsync(camera, new[] { fe.after.id }, "event", fe.after.id);
                    if (!snaps.TryGetValue(fe.after.id, out string snapshotPath))
                    {
                        Log("event", fe.after.id, camera, "Error: the snapshot is not ready");
                        return;
                    }

                    string aiPrompt = AiPrompt(fe.after.label == "person");
                    var imagePaths = new List<string> { snapshotPath };

                    int x = 1;
                    foreach (var chid in settings.telegram.chatids)
                    {
                        string tgcaption = fe.after.id + " " + L10n.Tg.T("caption.photo") + "\n" +
                                           L10n.Tg.T("caption.camera") + " " + fe.after.camera + "\n" +
                                           L10n.Tg.T("caption.object") + " " + rulabel + "\n" +
                                           L10n.Tg.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                           (fe.after.end_time.HasValue ? L10n.Tg.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                           L10n.Tg.T("caption.event") + " " + fe.after.id;

                        Message msg = await TgCall(() => bot.SendPhoto(
                            chatId: chid, disableNotification: NotifySilent,
                            photo: InputFile.FromStream(System.IO.File.OpenRead(snapshotPath)),
                            caption: tgcaption,
                            parseMode: ParseMode.Markdown),
                            "event", fe.after.id, camera);

                        await Task.Delay(100);
                        if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                        x++;

                        Log("event", fe.after.id, camera, "The snapshot was sent to telegram chat " + chid);

                        if (goFR && Cam(cami).fr)
                            frQueue.AddToQueue(new FRTask
                            {
                                ImagePaths = imagePaths,
                                AIPrompt = aiPrompt,
                                ChatId = long.Parse(chid),
                                MessageId = msg.MessageId,
                                Camera = camera,
                                EventId = fe.after.id,
                                OriginalCaption = tgcaption
                            });
                        else if (goAI && Cam(cami).ai)
                            aiQueue.AddToQueue(new AITask
                            {
                                ImagePaths = imagePaths,
                                Prompt = aiPrompt,
                                ChatId = long.Parse(chid),
                                MessageId = msg.MessageId,
                                Camera = camera,
                                EventId = fe.after.id,
                                OriginalCaption = tgcaption
                            });
                    }
                }
            }
            catch (Exception ex)
            {
                Log("event", fe.after.id, fe.after.camera, "Error in event new/update worker: " + ex.Message);
                Metrics.Inc("lookout_worker_errors_total", "worker", "event-new");
                RetryQueue.FailWorker("event-new", fe, attempt, fe.after.id, fe.after.camera, ex);
            }
        }


        private static async Task SendEventMediaAsync(
                                        FrigateEvent fe,
                                        string camera,
                                        int cami,
                                        string rulabel,
                                        List<IAlbumInputMedia> md,
                                        Dictionary<string, int> firstmessages,
                                        bool firstmessage,
                                        string tgcaption,
                                        List<DbRow> dl)
        {
            var parts = BuildFfmpegParts(fe.after.id, camera, dl);
            int partid = parts.Count;

            if (string.IsNullOrEmpty(tgcaption))
                tgcaption = L10n.Tg.T("caption.event") + " \t" + fe.after.id + "\n" +
                            L10n.Tg.T("caption.camera") + " " + fe.after.camera + "\n" +
                            L10n.Tg.T("caption.object") + " " + rulabel + "\n" +
                            L10n.Tg.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                            (fe.after.end_time.HasValue ? L10n.Tg.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "");

            var imagePaths = new List<string> { settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg" };
            string aiPrompt = AiPrompt(fe.after.label == "person");

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
                                "event", fe.after.id, camera);
                            await Task.Delay(100);
                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            x++;
                            Log("event", fe.after.id, camera, "The gif was sent to telegram chat " + chid);
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
                                fe.after.id + ((partid == 1) ? "" : "-part" + i) + ".mp4")));

                            if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                            {
                                int x = 1;
                                foreach (var chid in settings.telegram.chatids)
                                {
                                    if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                    Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md, disableNotification: NotifySilent), "event", fe.after.id, camera);
                                    await Task.Delay(100);
                                    firstmessages[chid] = msgs[0].MessageId;
                                    Log("event", fe.after.id, camera, "The snapshot and clip was sent to telegram chat " + chid);
                                    x++;

                                    if (goFR && Cam(cami).fr)
                                        frQueue.AddToQueue(new FRTask
                                        {
                                            ImagePaths = imagePaths,
                                            AIPrompt = aiPrompt,
                                            ChatId = long.Parse(chid),
                                            MessageId = firstmessages[chid],
                                            Camera = camera,
                                            EventId = fe.after.id,
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
                                            EventId = fe.after.id,
                                            OriginalCaption = tgcaption
                                        });
                                }
                            }
                        }
                        else
                        {
                            string cap = fe.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.Tg.T("caption.video");
                            int x = 1;
                            foreach (var chid in settings.telegram.chatids)
                            {
                                if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                await TgCall(() => bot.SendVideo(
                                    chatId: chid, disableNotification: NotifySilent,
                                    video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                    caption: cap,
                                    supportsStreaming: true,
                                    parseMode: ParseMode.Markdown,
                                    replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                    "event", fe.after.id, camera);
                                await Task.Delay(100);
                                Log("event", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i + " ") + "was sent to telegram chat " + chid);
                                x++;
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
                                ? fe.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.Tg.T("caption.video")
                                : fe.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.Tg.T("caption.video") + "\n" +
                                  L10n.Tg.T("caption.camera") + " " + fe.after.camera + "\n" +
                                  L10n.Tg.T("caption.object") + " " + rulabel + "\n" +
                                  L10n.Tg.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                  (fe.after.end_time.HasValue ? L10n.Tg.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "");

                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            await TgCall(() => bot.SendVideo(
                                chatId: chid, disableNotification: NotifySilent,
                                video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                caption: cap,
                                supportsStreaming: true,
                                parseMode: ParseMode.Markdown,
                                replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                "event", fe.after.id, camera);
                            await Task.Delay(100);
                            Log("event", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i + " ") + "was sent to telegram chat " + chid);
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


        async static Task FrigateEventEndWorker(FrigateEvent fe, int attempt = 0)
        {
            RetryQueue.BeginRun();
            NotifyContext.Begin(settings.frigate.cameras.Find(c => c.camera == fe.after.camera), fe.after.id);
            try
            {
                string camera = fe.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("event", fe.after.id, camera, "Start event end worker");

                bool firstmessage = false;
                string tgcaption = "";
                Dictionary<string, int> firstmessages = new Dictionary<string, int>();
                foreach (var chid in settings.telegram.chatids)
                    firstmessages.Add(chid, -1);

                string rulabel = L10n.Tg.Label(fe.after.label.ToLower())
                               + " (" + (fe.after.score * 100).ToString("0.00") + "%)";

                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();

                if (Cam(cami).snapshot && Cam(cami).snapshottrigger == "end")
                {
                    string snapPath = settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg";

                    int secs = 0;
                    while (secs <= settings.options.timeout)
                    {
                        if (System.IO.File.Exists(snapPath)) break;
                        secs += settings.options.retry;
                        await Task.Delay(settings.options.retry * 1000);
                    }

                    if (!System.IO.File.Exists(snapPath))
                        Log("event", fe.after.id, camera, "Snapshot not ready after timeout, skipping");
                    else
                    {
                        tgcaption = L10n.Tg.T("caption.review") + " \t" + fe.after.id + "\n" +
                                    L10n.Tg.T("caption.camera") + " " + fe.after.camera + "\n" +
                                    L10n.Tg.T("caption.object") + " " + rulabel + "\n" +
                                    L10n.Tg.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                    (fe.after.end_time.HasValue ? L10n.Tg.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "");

                        md.Add(new InputMediaPhoto(
                            new InputFileStream(System.IO.File.OpenRead(snapPath), fe.after.camera + "-" + fe.after.id + ".jpg"))
                        {
                            Caption = tgcaption,
                            ParseMode = ParseMode.Markdown
                        });

                        if ((md.Count > 0) && (!Cam(cami).sctogether || !Cam(cami).clip))
                        {
                            firstmessage = true;
                            int x = 1;
                            foreach (var chid in settings.telegram.chatids)
                            {
                                Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md, disableNotification: NotifySilent), "event", fe.after.id, camera);
                                await Task.Delay(100);
                                firstmessages[chid] = msgs[0].MessageId;
                                if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                x++;
                                Log("event", fe.after.id, camera, "The snapshot was sent to telegram chat " + chid);
                            }

                            if (goFR && Cam(cami).fr)
                                foreach (var chid in settings.telegram.chatids)
                                    frQueue.AddToQueue(new FRTask
                                    {
                                        ImagePaths = new List<string> { snapPath },
                                        AIPrompt = AiPrompt(fe.after.label == "person"),
                                        ChatId = long.Parse(chid),
                                        MessageId = firstmessages[chid],
                                        Camera = camera,
                                        EventId = fe.after.id,
                                        OriginalCaption = tgcaption
                                    });
                            else if (goAI && Cam(cami).ai)
                                foreach (var chid in settings.telegram.chatids)
                                    aiQueue.AddToQueue(new AITask
                                    {
                                        ImagePaths = new List<string> { snapPath },
                                        Prompt = AiPrompt(fe.after.label == "person"),
                                        ChatId = long.Parse(chid),
                                        MessageId = firstmessages[chid],
                                        Camera = camera,
                                        EventId = fe.after.id,
                                        OriginalCaption = tgcaption
                                    });
                        }
                    }
                }

                if (fe.after.has_clip && (Cam(cami).clip || Cam(cami).gif))
                {
                    int secs = 0;
                    var sqlq = (sql: new Queries().getEventQuery("event", true), id: fe.after.id, camera: fe.after.camera);
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
                            Log("event", fe.after.id, camera, "All recordings are ready");

                            if (Cam(cami).trueend)
                            {
                                var fes = new FrigateEvent { type = "trueend", before = fe.before, after = fe.after };
                                Log("event", fe.after.id, camera, "Sending the trueend event");
                                await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                    .WithTopic(settings.mqtt.eventstopic)
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

                            await SendEventMediaAsync(fe, camera, cami, rulabel, md, firstmessages, firstmessage, tgcaption, dl);
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
                            Log("event", fe.after.id, camera, "Timeout ended, video files were not ready. Trying to send everything Frigate has");
                            using var db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                            sqlq = (new Queries().getEventQuery("event", false), fe.after.id, fe.after.camera);
                            db.Open();
                            using var dr = RecordingsCommand(db, sqlq).ExecuteReader();
                            if (dr.HasRows)
                            {
                                Log("event", fe.after.id, camera, "All recordings are ready");

                                if (Cam(cami).trueend)
                                {
                                    var fes = new FrigateEvent { type = "trueend", before = fe.before, after = fe.after };
                                    Log("event", fe.after.id, camera, "Sending the trueend event");
                                    await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                        .WithTopic(settings.mqtt.eventstopic)
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

                                await SendEventMediaAsync(fe, camera, cami, rulabel, md, firstmessages, firstmessage, tgcaption, dl);
                            }
                        }
                        else
                            Log("event", fe.after.id, camera, "Timeout ended, video files were not ready");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("event", fe.after.id, fe.after.camera, "Error in event end worker: " + ex.Message);
                Metrics.Inc("lookout_worker_errors_total", "worker", "event-end");
                RetryQueue.FailWorker("event-end", fe, attempt, fe.after.id, fe.after.camera, ex);
            }
        }

    }
}
