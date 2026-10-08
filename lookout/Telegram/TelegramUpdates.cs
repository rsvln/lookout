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
        async static Task TgHandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.CallbackQuery is { } callback)
            {
                await TgHandleCallbackAsync(botClient, callback, cancellationToken);
                return;
            }

            if (update.Message is not { } message)
                return;
            if (message.Text is not { } messageText)
                return;

            Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Received message: " + messageText);

            if (!settings.telegram.chatids.Contains(message.Chat.Id.ToString()))
            {
                Log("tg", "", message.From.Id.ToString(), "Unauthorized access attempt from " + message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"));
                await TgCall(() => botClient.SendMessage(chatId: message.Chat.Id, text: L10n.Tg.T("tg.unauthorized"), cancellationToken: cancellationToken), "tg", "", message.Chat.Id.ToString());
                return;
            }

            string[] tokens = messageText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return;
            string command = tokens[0].ToLower();
            if (command.Contains('@'))
                command = command.Substring(0, command.IndexOf('@'));
            string[] args = tokens.Skip(1).ToArray();
            string who = message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")");

            if (command == "/last")
            {
                Log("tg", who, message.Chat.Id.ToString(), "Sending last");
                await TgSendLast(botClient, message.Chat.Id, args, cancellationToken);
            }

            if (command == "/stat")
            {
                Log("tg", who, message.Chat.Id.ToString(), "Sending stat");
                await TgSendStat(botClient, message.Chat.Id, null, args, cancellationToken);
            }

            if (command == "/mute")
                await TgMute(botClient, message.Chat.Id, who, args, cancellationToken);

            if (command == "/unmute")
                await TgUnmute(botClient, message.Chat.Id, who, args, cancellationToken);

            if (command == "/clip")
            {
                Log("tg", who, message.Chat.Id.ToString(), "Sending clip");
                await TgSendClip(botClient, message.Chat.Id, args.FirstOrDefault(), cancellationToken);
            }

            if (command == "/private" || command == "/help" || command == "/start")
            {
                Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Sending help");
                string helpText = L10n.Tg.T("tg.help") + "\n\n" + L10n.Tg.T("tg.help.version", VersionInfo.Version);
                await TgCall(() => botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: helpText,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken),
                    "tg", "", message.Chat.Id.ToString());
            }

            if (command == "/status")
            {
                Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Sending status");
                int? waitId = await TgSendWaitAsync(botClient, message.Chat.Id, L10n.Tg.T("tg.wait.status"), cancellationToken);
                using var db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                db.Open();
                using var dr = (new SqliteCommand(new Queries().getCamerasQuery(), db)).ExecuteReader();
                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();
                if (dr.HasRows)
                {
                    int i = 1;
                    while (dr.Read())
                    {
                        string rnd = RandomString(10);
                        string localPath = appLocation + "/" + dr["camera"].ToString() + "_" + rnd + ".jpg";
                        await DownloadFileAsync("http://" + settings.frigate.host + ":" + settings.frigate.port.ToString() + "/api/" + dr["camera"].ToString() + "/latest.jpg", localPath);

                        md.Add(new InputMediaPhoto(
                            new InputFileStream(System.IO.File.OpenRead(localPath), dr["camera"].ToString() + "_" + rnd + ".jpg"))
                        {
                            Caption = ((i == 1) || (i % 11 == 0)) ? L10n.Tg.T("tg.status.caption") : null
                        });

                        if (i % 10 == 0)
                        {
                            await TgCall(() => botClient.SendMediaGroup(chatId: message.Chat.Id, media: md.ToList()), "tg", "", message.Chat.Id.ToString());
                            md.Clear();
                            Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Status batch sent");
                        }

                        System.IO.File.Delete(localPath);
                        i++;
                    }
                }
                dr.Close();
                db.Close();

                if (md.Count > 0)
                {
                    await TgCall(() => botClient.SendMediaGroup(chatId: message.Chat.Id, media: md.ToList()), "tg", "", message.Chat.Id.ToString());
                    Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Status sent");
                }
                await TgDeleteWaitAsync(botClient, message.Chat.Id, waitId);
            }
        }

        public static Dictionary<string, string> emojiobj = new Dictionary<string, string>()
                                                    {
                                                        { "car", "🚗" },
                                                        { "person", "👤" },
                                                        { "dog", "🐕" },
                                                        { "cat", "🐈" },
                                                        { "bird", "🐦" }
                                                    };

        static string EmojiLabel(string label) => emojiobj.TryGetValue(label, out var em) ? em : "•";

        static async Task TgHandleCallbackAsync(ITelegramBotClient botClient, CallbackQuery callback, CancellationToken cancellationToken)
        {
            if (callback.Message == null || callback.Data == null)
                return;
            long chatId = callback.Message.Chat.Id;
            string who = callback.From.Id + (string.IsNullOrEmpty(callback.From.Username) ? "" : " (@" + callback.From.Username + ")");

            if (!settings.telegram.chatids.Contains(chatId.ToString()))
            {
                Log("tg", who, chatId.ToString(), "Unauthorized callback from " + who);
                return;
            }

            Log("tg", who, chatId.ToString(), "Received callback: " + callback.Data);
            try { await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken); }
            catch (ApiRequestException) { }

            // last|<camera or label>   stat|<period>|<camera>|<label>   clip|<event id>   mute|<camera>|<minutes>
            string[] parts = callback.Data.Split('|');
            if (parts[0] == "clip" && parts.Length == 2)
                await TgSendClip(botClient, chatId, parts[1], cancellationToken);
            else if (parts[0] == "mute" && parts.Length == 3 && int.TryParse(parts[2], out int muteMinutes) && muteMinutes > 0
                     && settings.frigate.cameras.Any(c => c.camera == parts[1]))
                await TgMuteCameraAsync(botClient, chatId, who, parts[1], TimeSpan.FromMinutes(muteMinutes), cancellationToken);
            else if (parts[0] == "last" && parts.Length == 2)
                await TgSendLast(botClient, chatId, new[] { parts[1] }, cancellationToken);
            else if (parts[0] == "stat" && parts.Length == 4)
                await TgSendStat(botClient, chatId, callback.Message.Id,
                                 new[] { parts[1], parts[2], parts[3] }.Where(s => s.Length > 0).ToArray(), cancellationToken);
        }

    }
}
