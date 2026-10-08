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
        static string LiveSnapshotDir => appLocation + "/live";

        // Frigate writes {camera}-{id}.jpg to clips only when an event ends, but a review can end while one of its
        // detections is still going on (e.g. a parked car). For such an event the current best frame is taken from
        // the Frigate API and saved to LiveSnapshotDir. Returns null while nothing is available yet.
        static async Task<string> ResolveSnapshotAsync(string camera, string eventId, string type, string logId)
        {
            string clip = settings.frigate.clipspath + "/" + camera + "-" + eventId + ".jpg";
            if (System.IO.File.Exists(clip))
                return clip;

            // Ended: Frigate serves the snapshot from its clips folder once it is written (404 until then). Getting it here
            // means the file exists but clipspath doesn't show it (volume not mounted), so the API copy is used.
            var ev = StatsService.GetEvent(eventId);
            bool ended = ev != null && ev.end_time != null;

            var bytes = await StatsService.GetFrigateSnapshotAsync(eventId);
            if (bytes == null)
                return null;
            if (ended)
                StatsService.WarnClipsPathOnce();

            Directory.CreateDirectory(LiveSnapshotDir);
            foreach (var old in Directory.GetFiles(LiveSnapshotDir).Where(f => System.IO.File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)))
                try { System.IO.File.Delete(old); } catch { }

            string path = LiveSnapshotDir + "/" + camera + "-" + eventId + ".jpg";
            await System.IO.File.WriteAllBytesAsync(path, bytes);
            Log(type, logId, camera, ended
                ? "Snapshot of event " + eventId + " is not in clipspath, using the one from Frigate's API"
                : "Event " + eventId + " is still in progress, using its current snapshot from Frigate");
            return path;
        }

        // Waits (up to options.timeout) until every event has a snapshot; returns event id -> snapshot path for those found.
        static async Task<Dictionary<string, string>> WaitSnapshotsAsync(string camera, IEnumerable<string> eventIds, string type, string logId)
        {
            var ids = eventIds.ToList();
            var found = new Dictionary<string, string>();
            int secs = 0;
            while (true)
            {
                foreach (var id in ids.Where(i => !found.ContainsKey(i)).ToList())
                {
                    string path = await ResolveSnapshotAsync(camera, id, type, logId);
                    if (path != null)
                        found[id] = path;
                }
                if (found.Count == ids.Count || secs >= settings.options.timeout)
                    return found;
                secs += settings.options.retry;
                await Task.Delay(settings.options.retry * 1000);
            }
        }

        // Recording segments in one piece when they total at most `check` bytes; otherwise consecutive groups of at most
        // `split` bytes each (a single segment bigger than that still gets a group of its own).
        internal static List<List<DbRow>> SplitClipChunks(List<DbRow> dl, long check, long split)
        {
            var chunks = new List<List<DbRow>>();
            if (dl.Sum(x => x.size) <= check)
            {
                chunks.Add(dl);
                return chunks;
            }

            int start = 0;
            while (start < dl.Count)
            {
                long currSize = 0;
                int end = start;
                for (int j = start; j < dl.Count; j++)
                {
                    if (currSize + dl[j].size <= split)
                    {
                        currSize += dl[j].size;
                        end = j;
                    }
                    else
                    {
                        if (j == start) end = j;
                        break;
                    }
                }
                chunks.Add(dl.GetRange(start, end - start + 1));
                start = end + 1;
            }
            return chunks;
        }

        private static List<(int partId, int totalParts, string path)> BuildFfmpegParts(string id, string camera, List<DbRow> dl)
        {
            var result = new List<(int, int, string)>();
            long totalSize = dl.Sum(x => x.size);

            if (totalSize > settings.telegram.clipsizecheck)
                Log("review", id, camera, "The clip size exceeds " + settings.telegram.clipsizecheck + " bytes, will be splitted");
            var chunks = SplitClipChunks(dl, settings.telegram.clipsizecheck, settings.telegram.clipsizesplit);

            int total = chunks.Count;
            for (int i = 0; i < chunks.Count; i++)
            {
                int partId = i + 1;
                string suffix = (total == 1) ? "" : "-part" + partId;
                string txtPath = Path.Combine(appLocation, id + suffix + ".txt");
                string mp4Path = Path.Combine(appLocation, id + suffix + ".mp4");

                System.IO.File.WriteAllLines(txtPath, chunks[i].Select(x => "file '" + x.realpath + "'"));
                RunFfmpeg(txtPath, mp4Path);
                result.Add((partId, total, mp4Path));
            }

            Log("review", id, camera, (total == 1) ? "File is prepared by ffmpeg for sending" : total + " files are prepared by ffmpeg for sending");

            return result;
        }

        private static void RunFfmpeg(string txtPath, string mp4Path)
        {
            string args = $"-y -hide_banner -loglevel error -f concat -safe 0 -i \"{txtPath}\" -c copy \"{mp4Path}\"";
            using var p = Process.Start("ffmpeg", args);
            p.WaitForExit();
            System.IO.File.Delete(txtPath);
        }
        private static string RunFfmpegGif(string mp4Path, int width = 320)
        {
            string gifPath = mp4Path.Replace(".mp4", ".gif");
            string args = $"-y -hide_banner -loglevel error -i \"{mp4Path}\" -r 8 -vf \"setpts=0.12*PTS,scale={width}:-1\" -loop 0 \"{gifPath}\"";
            using var p = Process.Start("ffmpeg", args);
            p.WaitForExit();
            return gifPath;
        }

    }
}
