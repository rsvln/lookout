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
        static async Task TgSendLast(ITelegramBotClient botClient, long chatId, string[] args, CancellationToken cancellationToken)
        {
            int? waitId = null;
            try
            {
                CommandFilter f = StatsService.ParseArgs(args);
                if (f.unknown.Count > 0)
                {
                    await TgSendUnknown(botClient, chatId, f.unknown, cancellationToken);
                    return;
                }

                waitId = await TgSendWaitAsync(botClient, chatId, L10n.Tg.T("tg.wait.last"), cancellationToken);

                // No camera given: last N events of every camera (N defaults to 1). Camera given: last N of that camera (N defaults to 5).
                bool overview = f.camera == null && f.label == null && f.limit == null;
                bool perCamera = f.camera == null;
                int limit = Math.Clamp(f.limit ?? (perCamera ? 1 : 5), 1, settings.telegram.mediagrouplimit);
                List<EventRow> rows = perCamera ? StatsService.GetLastPerCamera(limit, f.label) : StatsService.GetLast(f.camera, f.label, limit);

                if (rows.Count == 0)
                    await TgCall(() => botClient.SendMessage(chatId, L10n.Tg.T("tg.last.none"), cancellationToken: cancellationToken), "tg", "", chatId.ToString());

                var snaps = new List<(EventRow row, byte[] jpg)>();
                foreach (var r in rows)
                    snaps.Add((r, await StatsService.GetSnapshotAsync(r)));
                var withoutSnap = snaps.Where(s => s.jpg == null).Select(s => s.row).ToList();

                // One album per camera when several events per camera were asked for, otherwise one shared album.
                string filterTitle = f.label != null ? " · " + EmojiLabel(f.label) + " " + WebUtility.HtmlEncode(L10n.Tg.Label(f.label)) : "";
                var groups = perCamera && limit > 1
                    ? snaps.Where(s => s.jpg != null).GroupBy(s => s.row.camera)
                           .Select(g => (title: "<b>📷 " + WebUtility.HtmlEncode(g.Key) + "</b>" + filterTitle, items: g.ToList()))
                    : new[] { (title: (perCamera ? "<b>" + L10n.Tg.T("tg.last.title_all") + "</b>" : "<b>📷 " + WebUtility.HtmlEncode(f.camera) + "</b>") + filterTitle,
                                items: snaps.Where(s => s.jpg != null).ToList()) };

                foreach (var (title, items) in groups)
                foreach (var chunkSnaps in items.Chunk(settings.telegram.mediagrouplimit))
                {
                    var chunk = chunkSnaps.Select(s => s.row).ToArray();
                    // Telegram shows an album caption only when a single item has one, so all events are listed on the first photo.
                    string caption = title + "\n" +
                                     string.Join("\n", chunk.Select((r, i) => (i + 1) + ". " + FormatEventLine(r)));
                    var streams = chunkSnaps.Select(s => (Stream)new MemoryStream(s.jpg)).ToList();
                    try
                    {
                        if (chunk.Length == 1)
                            await TgCall(() => botClient.SendPhoto(chatId, new InputFileStream(streams[0], chunk[0].camera + "-" + chunk[0].id + ".jpg"),
                                                                    caption: caption, parseMode: ParseMode.Html, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                        else
                        {
                            var md = chunk.Select((r, i) => (IAlbumInputMedia)new InputMediaPhoto(new InputFileStream(streams[i], r.camera + "-" + r.id + ".jpg"))
                            {
                                Caption = i == 0 ? caption : null,
                                ParseMode = ParseMode.Html
                            }).ToList();
                            await TgCall(() => botClient.SendMediaGroup(chatId, md, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                        }
                    }
                    finally
                    {
                        streams.ForEach(s => s.Dispose());
                    }
                }

                if (withoutSnap.Count > 0)
                {
                    string text = "<b>" + L10n.Tg.T("tg.last.no_snapshot") + "</b>\n" + string.Join("\n", withoutSnap.Select(FormatEventLine));
                    await TgCall(() => botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                }

                if (overview)
                {
                    MetaResult meta = StatsService.GetMeta();
                    var buttons = new[] { 3, 5 }.Select(n => (text: L10n.Tg.T("tg.last.per_camera", n), data: CallbackData("last|" + n)))
                                  .Concat(meta.cameras.Select(c => (text: "📷 " + c, data: CallbackData("last|" + c))))
                                  .Concat(meta.labels.Select(l => (text: EmojiLabel(l) + " " + L10n.Tg.Label(l), data: CallbackData("last|" + l))))
                                  .Where(b => b.data != null)
                                  .Select(b => InlineKeyboardButton.WithCallbackData(b.text, b.data))
                                  .Chunk(3);
                    await TgCall(() => botClient.SendMessage(chatId, L10n.Tg.T("tg.last.pick"),
                                                             replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                }
            }
            catch (Exception ex)
            {
                Log("tg", "", chatId.ToString(), "Error: /last failed: " + ex.Message);
                await TgCall(() => botClient.SendMessage(chatId, L10n.Tg.T("tg.error", ex.Message), cancellationToken: cancellationToken), "tg", "", chatId.ToString());
            }
            finally
            {
                await TgDeleteWaitAsync(botClient, chatId, waitId);
            }
        }

        // Sends the stats message, or edits `editMessageId` in place when a period button was pressed.
        static async Task TgSendStat(ITelegramBotClient botClient, long chatId, int? editMessageId, string[] args, CancellationToken cancellationToken)
        {
            try
            {
                CommandFilter f = StatsService.ParseArgs(args);
                if (f.unknown.Count > 0)
                {
                    await TgSendUnknown(botClient, chatId, f.unknown, cancellationToken);
                    return;
                }

                string suffix = "|" + (f.camera ?? "") + "|" + (f.label ?? "");
                var periods = new[] { "24h", "today", "7d", "30d" }.Select(p => (L10n.Tg.T("tg.stat.btn." + p), p));
                var keyboard = new InlineKeyboardMarkup(periods
                    .Select(p => InlineKeyboardButton.WithCallbackData(p.Item1, CallbackData("stat|" + p.Item2 + suffix) ?? "stat|" + p.Item2 + "||")));

                // Visible reaction right away: a period button turns the message into "calculating…", a command gets a wait message.
                StatsService.TryParsePeriod(f.period, out _, out string periodTitle);
                string waitText = L10n.Tg.T("tg.wait.stat", periodTitle);
                int? waitId = null;
                if (editMessageId.HasValue)
                {
                    try
                    {
                        await TgCall(() => botClient.EditMessageText(chatId, editMessageId.Value, "⏳ " + waitText, replyMarkup: keyboard,
                                                                     cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                    }
                    catch (ApiRequestException ex) when (ex.Message.Contains("not modified")) { }
                }
                else
                    waitId = await TgSendWaitAsync(botClient, chatId, waitText, cancellationToken);

                StatsResult st = StatsService.GetStats(f.period, f.camera, f.label);
                string text = FormatStat(st) + "\n<i>" + L10n.Tg.T("tg.stat.updated", DateTime.UtcNow.AddMinutes(settings.options.timeoffset).ToString("HH:mm:ss")) + "</i>";

                if (editMessageId.HasValue)
                    await TgCall(() => botClient.EditMessageText(chatId, editMessageId.Value, text, parseMode: ParseMode.Html,
                                                                 replyMarkup: keyboard, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                else
                {
                    await TgCall(() => botClient.SendMessage(chatId, text, parseMode: ParseMode.Html,
                                                             replyMarkup: keyboard, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                    await TgDeleteWaitAsync(botClient, chatId, waitId);
                }
            }
            catch (Exception ex)
            {
                Log("tg", "", chatId.ToString(), "Error: /stat failed: " + ex.Message);
                await TgCall(() => botClient.SendMessage(chatId, L10n.Tg.T("tg.error", ex.Message), cancellationToken: cancellationToken), "tg", "", chatId.ToString());
            }
        }

        // Slow commands first post a "please wait" message (as y2tav does) and delete it once the result is sent.
        static async Task<int?> TgSendWaitAsync(ITelegramBotClient botClient, long chatId, string text, CancellationToken cancellationToken)
        {
            try
            {
                var msg = await TgCall(() => botClient.SendMessage(chatId, "⏳ " + text, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                return msg.MessageId;
            }
            catch (Exception ex)
            {
                Log("tg", "", chatId.ToString(), "Failed to send the wait message: " + ex.Message);
                return null;
            }
        }

        static async Task TgDeleteWaitAsync(ITelegramBotClient botClient, long chatId, int? messageId)
        {
            if (messageId == null)
                return;
            try { await botClient.DeleteMessage(chatId, messageId.Value); }
            catch (Exception ex) { Log("tg", "", chatId.ToString(), "Failed to delete the wait message: " + ex.Message); }
        }

        static async Task TgSendUnknown(ITelegramBotClient botClient, long chatId, List<string> unknown, CancellationToken cancellationToken)
        {
            MetaResult meta = StatsService.GetMeta();
            string text = L10n.Tg.T("tg.unknown", WebUtility.HtmlEncode(string.Join(", ", unknown))) + "\n\n" +
                          "<b>" + L10n.Tg.T("tg.unknown.cameras") + "</b> " + WebUtility.HtmlEncode(string.Join(", ", meta.cameras)) + "\n" +
                          "<b>" + L10n.Tg.T("tg.unknown.objects") + "</b> " + WebUtility.HtmlEncode(string.Join(", ", meta.labels)) + "\n" +
                          "<b>" + L10n.Tg.T("tg.unknown.periods") + "</b> 24h, 7d, 30d, today";
            await TgCall(() => botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
        }

        // Telegram limits callback data to 64 bytes.
        static string CallbackData(string data) => Encoding.UTF8.GetByteCount(data) <= 64 ? data : null;

        // Time only for today, date + time otherwise.
        static string FormatShortTime(double unix, string todayFormat)
        {
            DateTime t = StatsService.ToLocal(unix);
            return t.Date == DateTime.UtcNow.AddMinutes(settings.options.timeoffset).Date ? t.ToString(todayFormat) : t.ToString("dd.MM HH:mm");
        }

        static string FormatEventLine(EventRow r)
        {
            string time = FormatShortTime(r.start_time, "HH:mm:ss");
            return WebUtility.HtmlEncode(r.camera) + " · " + EmojiLabel(r.label) + " " + WebUtility.HtmlEncode(L10n.Tg.Label(r.label)) +
                   (r.sub_label != null ? " (" + WebUtility.HtmlEncode(r.sub_label) + ")" : "") +
                   " · " + Math.Round(r.score * 100) + "% · " + time + (r.end_time == null ? " · ⏳ " + L10n.Tg.T("tg.last.in_progress") : "") +
                   (r.zones.Count > 0 ? " · " + WebUtility.HtmlEncode(string.Join(", ", r.zones)) : "");
        }

        static string Sparkline(IEnumerable<int> values)
        {
            const string bars = "▁▂▃▄▅▆▇█";
            var list = values.ToList();
            int max = list.DefaultIfEmpty(0).Max();
            return new string(list.Select(v => v == 0 ? ' ' : bars[Math.Min(bars.Length - 1, (int)Math.Ceiling((double)v / max * bars.Length) - 1)]).ToArray());
        }

        static string FormatStat(StatsResult st)
        {
            var sb = new StringBuilder();
            sb.Append("<b>").Append(L10n.Tg.T("tg.stat.title", st.period)).Append("</b>");
            if (st.camera != null) sb.Append(" · 📷 ").Append(WebUtility.HtmlEncode(st.camera));
            if (st.label != null) sb.Append(" · ").Append(EmojiLabel(st.label)).Append(' ').Append(WebUtility.HtmlEncode(L10n.Tg.Label(st.label)));
            sb.Append('\n');
            sb.Append(L10n.Tg.T("tg.stat.summary", st.total, st.alerts, st.detections)).Append('\n');

            if (st.total == 0)
                return sb.Append("\n" + L10n.Tg.T("tg.stat.empty")).ToString();

            sb.Append("\n<b>" + L10n.Tg.T("tg.stat.by_object") + "</b>\n<pre>");
            int lw = st.labels.Max(l => L10n.Tg.Label(l).Length);
            foreach (var l in st.labels)
                sb.Append(EmojiLabel(l)).Append(' ').Append(WebUtility.HtmlEncode(L10n.Tg.Label(l).PadRight(lw))).Append(' ').Append(st.labelTotals[l].ToString().PadLeft(5)).Append('\n');
            sb.Append("</pre>");

            if (st.camera == null)
            {
                // A grid: the cells hold only numbers, emoji go to the header row alone, since their width varies
                // between clients. Up to 4 object columns; with more, the 3 most frequent plus "•" for the rest,
                // so the table still fits a phone screen.
                var labels = st.labels.Count <= 4 ? st.labels : st.labels.Take(3).ToList();
                var columns = new List<(string head, int headWidth, Func<string, int> value)>
                {
                    ("Σ", 1, c => st.matrix[c].Values.Sum())
                };
                foreach (var l in labels)
                    columns.Add((EmojiLabel(l), emojiobj.ContainsKey(l) ? 2 : 1, c => st.matrix[c].GetValueOrDefault(l)));
                if (st.labels.Count > 4)
                    columns.Add(("•", 1, c => st.matrix[c].Where(kv => !labels.Contains(kv.Key)).Sum(kv => kv.Value)));

                static string Cell(int v) => v == 0 ? "·" : v.ToString();
                int nameWidth = st.cameras.Max(c => c.Length);
                var widths = columns.Select(col => Math.Max(4, st.cameras.Max(c => Cell(col.value(c)).Length))).ToList();

                sb.Append("\n<b>" + L10n.Tg.T("tg.stat.by_camera") + "</b>\n<pre>");
                sb.Append(new string(' ', nameWidth));
                for (int i = 0; i < columns.Count; i++)
                    sb.Append(' ').Append(new string(' ', widths[i] - columns[i].headWidth)).Append(columns[i].head);
                sb.Append("    ⏱\n");
                foreach (var c in st.cameras)
                {
                    sb.Append(WebUtility.HtmlEncode(c.PadRight(nameWidth)));
                    for (int i = 0; i < columns.Count; i++)
                        sb.Append(' ').Append(Cell(columns[i].value(c)).PadLeft(widths[i]));
                    // Always 5 characters: time for today, date for earlier days.
                    if (st.lastByCamera.TryGetValue(c, out double last))
                    {
                        DateTime t = StatsService.ToLocal(last);
                        bool today = t.Date == DateTime.UtcNow.AddMinutes(settings.options.timeoffset).Date;
                        sb.Append(' ').Append(t.ToString(today ? "HH:mm" : "dd.MM"));
                    }
                    sb.Append('\n');
                }
                sb.Append("</pre>");
            }
            else if (st.lastByCamera.TryGetValue(st.camera, out double last))
                sb.Append(L10n.Tg.T("tg.stat.last_event", FormatShortTime(last, "HH:mm:ss"))).Append('\n');

            sb.Append("\n<b>" + L10n.Tg.T("tg.stat.by_hour") + "</b> · " + L10n.Tg.T("tg.stat.peak") + " ").Append(st.peakHour.ToString("00")).Append(":00–").Append(((st.peakHour + 1) % 24).ToString("00")).Append(":00\n");
            sb.Append("<pre>").Append(Sparkline(st.hours)).Append("\n0     6     12    18   23</pre>");

            if (st.days.Count > 2)
            {
                sb.Append("\n<b>" + L10n.Tg.T("tg.stat.by_day") + "</b> · ").Append(DateTime.Parse(st.days[0].day).ToString("dd.MM")).Append(" – ")
                  .Append(DateTime.Parse(st.days[^1].day).ToString("dd.MM")).Append('\n');
                sb.Append("<pre>").Append(Sparkline(st.days.Select(d => d.count))).Append("</pre>");
            }
            return sb.ToString();
        }

        static Task TgHandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
            {
                var ErrorMessage = exception switch
                {
                    ApiRequestException
                    apiRequestException => $"Telegram API Error: [{apiRequestException.ErrorCode}] {apiRequestException.Message}",
                    _ => exception.ToString()
                };

                Log("app", "", "", ErrorMessage);
                return Task.CompletedTask;
            }

    }
}
