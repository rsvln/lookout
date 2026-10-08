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
        // `objects` filter of a camera: no list = everything passes; otherwise the label must be listed and
        // the score (0..1 from Frigate) must reach its `percent`.
        public static bool ObjectPasses(Camera cam, string label, double score)
        {
            if (cam.objects == null || cam.objects.Count == 0)
                return true;
            var obj = cam.objects.FirstOrDefault(o => o.label == label);
            return obj != null && score * 100 >= obj.percent;
        }

        // Events are judged by their best score so far (top_score), falling back to the current one.
        internal static bool EventPasses(Camera cam, BeforeAfterFE ev)
        {
            double score = Math.Max(ev.top_score, ev.score);
            if (ObjectPasses(cam, ev.label, score))
                return true;
            var obj = cam.objects.FirstOrDefault(o => o.label == ev.label);
            if (obj != null)
                Log("event", ev.id, ev.camera, $"Skipped: {ev.label} {Math.Round(score * 100)}% < {obj.percent}%");
            return false;
        }

        // Review messages carry no scores, so the thresholds are checked against the top_score of the review's
        // detections in Frigate's DB. Detections not in the DB yet (an early "new") are judged by label only.
        internal static bool ReviewPasses(Camera cam, FrigateReview fr)
        {
            if (cam.objects == null || cam.objects.Count == 0)
                return true;
            var labels = cam.objects.Select(o => o.label).ToList();
            if (fr.after.data?.objects == null || !fr.after.data.objects.Intersect(labels).Any())
                return false;

            List<EventRow> events;
            try
            {
                events = (fr.after.data.detections ?? new List<string>()).Select(StatsService.GetEvent).Where(e => e != null).ToList();
            }
            catch (Exception ex)
            {
                Log("review", fr.after.id, fr.after.camera, "Cannot check object thresholds, passing by label: " + ex.Message);
                return true;
            }
            if (events.Count == 0 || events.Any(e => ObjectPasses(cam, e.label, e.score)))
                return true;

            Log("review", fr.after.id, fr.after.camera, "Skipped: " + string.Join(", ", events
                .Where(e => labels.Contains(e.label))
                .Select(e => $"{e.label} {Math.Round(e.score * 100)}% < {cam.objects.First(o => o.label == e.label).percent}%")));
            return false;
        }

    }
}
