using System.Net;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Lookout
{
    // Buttons under notifications and the /mute, /unmute and /clip commands.
    internal partial class Program
    {
        // options.buttons: right after the first message of a notification reaches a chat, a small reply with the buttons
        // follows it (albums can't carry buttons themselves). Runs inside TgCall, so it must not call TgCall.
        static async Task TgAddActionsAsync(object sent)
        {
            var ctx = NotifyContext.Current;
            if (ctx == null || settings.options?.buttons != true || string.IsNullOrEmpty(ctx.ActionEventId))
                return;
            Message first = sent as Message ?? (sent as Message[])?.FirstOrDefault();
            if (first == null || !ctx.ActionsSent.Add(first.Chat.Id))
                return;

            var row = new List<InlineKeyboardButton>();
            string clip = CallbackData("clip|" + ctx.ActionEventId);
            if (clip != null)
                row.Add(InlineKeyboardButton.WithCallbackData(L10n.Tg.T("tg.btn.clip"), clip));

            string baseUrl = settings.web?.publicurl?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                row.Add(InlineKeyboardButton.WithUrl(L10n.Tg.T("tg.btn.open"), baseUrl + "/event/" + Uri.EscapeDataString(ctx.ActionEventId)));

            string mute = ctx.Camera == null ? null : CallbackData("mute|" + ctx.Camera + "|60");
            if (mute != null)
                row.Add(InlineKeyboardButton.WithCallbackData(L10n.Tg.T("tg.btn.mute"), mute));

            if (row.Count == 0)
                return;
            try
            {
                await bot.SendMessage(chatId: first.Chat.Id, text: L10n.Tg.T("tg.actions"),
                                      replyParameters: new ReplyParameters { MessageId = first.MessageId },
                                      replyMarkup: new InlineKeyboardMarkup(row), disableNotification: true);
            }
            catch (Exception ex)
            {
                Log("tg", ctx.ActionEventId, ctx.Camera, "Failed to send the buttons: " + ex.Message);
            }
        }

        static Task TgReplyAsync(ITelegramBotClient botClient, long chatId, string text, CancellationToken cancellationToken) =>
            TgCall(() => botClient.SendMessage(chatId, text, cancellationToken: cancellationToken), "tg", "", chatId.ToString());

        // 90 -> "90 min", 120 -> "2 h", 3 days -> "3 d"
        internal static string FormatDuration(TimeSpan d)
        {
            double minutes = d.TotalMinutes;
            if (minutes % 1440 == 0) return L10n.Tg.T("period.days", (int)(minutes / 1440));
            if (minutes % 60 == 0) return L10n.Tg.T("period.hours", (int)(minutes / 60));
            return L10n.Tg.T("period.minutes", (int)minutes);
        }

        // /mute [camera] [duration]: no camera mutes all of them, no duration is one hour.
        static async Task TgMute(ITelegramBotClient botClient, long chatId, string who, string[] args, CancellationToken cancellationToken)
        {
            string camera = null;
            TimeSpan? duration = null;
            var unknown = new List<string>();
            var cameras = settings.frigate.cameras.Select(c => c.camera).ToList();
            foreach (var a in args)
            {
                string cam = cameras.FirstOrDefault(c => c.Equals(a, StringComparison.OrdinalIgnoreCase));
                if (cam != null) camera = cam;
                else if (MuteService.TryParseDuration(a, out var d)) duration = d;
                else unknown.Add(a);
            }
            if (unknown.Count > 0)
            {
                await TgReplyAsync(botClient, chatId, L10n.Tg.T("tg.mute.unknown", string.Join(", ", unknown), string.Join(", ", cameras)), cancellationToken);
                return;
            }
            await TgMuteCameraAsync(botClient, chatId, who, camera, duration ?? TimeSpan.FromHours(1), cancellationToken);
        }

        static async Task TgMuteCameraAsync(ITelegramBotClient botClient, long chatId, string who, string camera, TimeSpan duration, CancellationToken cancellationToken)
        {
            var end = MuteService.Mute(camera, duration);
            Log("tg", who, chatId.ToString(), "Muted " + (camera ?? "all cameras") + " for " + (int)duration.TotalMinutes + " min");
            await TgReplyAsync(botClient, chatId, L10n.Tg.T("tg.mute.done", camera ?? L10n.Tg.T("tg.mute.all"), FormatDuration(duration),
                                                          end.AddMinutes(settings.options.timeoffset).ToString("dd.MM HH:mm")), cancellationToken);
        }

        static async Task TgUnmute(ITelegramBotClient botClient, long chatId, string who, string[] args, CancellationToken cancellationToken)
        {
            var cameras = settings.frigate.cameras.Select(c => c.camera).ToList();
            string camera = null;
            if (args.Length > 0)
            {
                camera = cameras.FirstOrDefault(c => c.Equals(args[0], StringComparison.OrdinalIgnoreCase));
                if (camera == null)
                {
                    await TgReplyAsync(botClient, chatId, L10n.Tg.T("tg.mute.unknown", args[0], string.Join(", ", cameras)), cancellationToken);
                    return;
                }
            }
            bool lifted = MuteService.Unmute(camera, cameras);
            Log("tg", who, chatId.ToString(), "Unmuted " + (camera ?? "all cameras"));
            await TgReplyAsync(botClient, chatId, lifted
                ? L10n.Tg.T("tg.unmute.done", camera ?? L10n.Tg.T("tg.mute.all"))
                : L10n.Tg.T("tg.unmute.none"), cancellationToken);
        }

        // /clip <id> and the Clip button: the id of an event, or of a review (its first event is used).
        static async Task TgSendClip(ITelegramBotClient botClient, long chatId, string id, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                await TgReplyAsync(botClient, chatId, L10n.Tg.T("tg.clip.usage"), cancellationToken);
                return;
            }
            int? waitId = null;
            try
            {
                id = id.Trim();
                var ev = StatsService.GetEvent(id);
                if (ev == null)
                {
                    string first = StatsService.GetReviewDetections(id).FirstOrDefault();
                    ev = first == null ? null : StatsService.GetEvent(first);
                }
                if (ev == null)
                {
                    await TgReplyAsync(botClient, chatId, L10n.Tg.T("tg.clip.notfound", id), cancellationToken);
                    return;
                }

                waitId = await TgSendWaitAsync(botClient, chatId, L10n.Tg.T("tg.wait.clip"), cancellationToken);
                string path = await StatsService.GetClipPathAsync(ev);
                if (path == null)
                {
                    await TgReplyAsync(botClient, chatId, L10n.Tg.T("tg.clip.none"), cancellationToken);
                    return;
                }
                long size = new FileInfo(path).Length;
                if (settings.telegram.clipsizecheck > 0 && size > settings.telegram.clipsizecheck)
                {
                    await TgReplyAsync(botClient, chatId, L10n.Tg.T("tg.clip.toolarge", size / 1024 / 1024), cancellationToken);
                    return;
                }

                using var fs = System.IO.File.OpenRead(path);
                string caption = "<b>📷 " + WebUtility.HtmlEncode(ev.camera) + "</b>\n" + FormatEventLine(ev);
                await TgCall(() => botClient.SendVideo(chatId, InputFile.FromStream(fs, ev.camera + "-" + ev.id + ".mp4"),
                                                       caption: caption, parseMode: ParseMode.Html, supportsStreaming: true,
                                                       cancellationToken: cancellationToken), "tg", ev.id, ev.camera);
                Log("tg", "", chatId.ToString(), "Clip of " + ev.id + " sent");
            }
            catch (Exception ex)
            {
                Log("tg", "", chatId.ToString(), "Error: /clip failed: " + ex.Message);
                await TgReplyAsync(botClient, chatId, L10n.Tg.T("tg.error", ex.Message), cancellationToken);
            }
            finally
            {
                await TgDeleteWaitAsync(botClient, chatId, waitId);
            }
        }
    }
}
