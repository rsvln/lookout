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
        public static SettingsFile settings;
        public static ITelegramBotClient bot;
        static CancellationTokenSource tgPollingCts;
        public static SemaphoreSlim tgSemaphore = new SemaphoreSlim(1, 1);
        public static MqttClientFactory mqttFactory;
        public static IMqttClient mqttClient;
        public static MqttClientOptions mqttOptions;
        public static string appLocation;
        public static AIQueueService aiQueue;
        public static FRQueueService frQueue;
        public static bool goAI = false;
        public static bool goFR = false;

        static async Task Main(string[] args)
        {
            appLocation = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            settings = new SettingsFile();
            string fs;
            if (args.Length == 0)
                fs = "/etc/lookout/lookout.yml";
            else
                fs = args[0];
            try
            {
                settings = (new DeserializerBuilder()
                                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                                .Build())
                           .Deserialize<SettingsFile>(System.IO.File.ReadAllText(fs));
            }
            catch
            {
                ConsoleLog("app", "", "", "Bad settings file. Exit");
                return;
            }

            Log("app", "", "", "Lookout v" + VersionInfo.Informational + " started");
            WebUi.Start(fs);
            StatsService.StartClipCacheCleaner();
            await Initialize();
            Thread.Sleep(Timeout.Infinite);
        }

        public static async Task Initialize()
        {
            var loc = settings.options?.locale ?? new LocaleSettings();
            L10n.Load(loc.web, loc.telegram, loc.ai);
            Log("app", "", "", "Languages: web " + L10n.Web.Locale + ", telegram " + L10n.Tg.Locale + ", ai " + L10n.Ai.Locale);

            RegisterRetryHandlers();
            RetryQueue.Start();
            NotifierHub.Reload(settings.notifiers);

            if (goAI) { aiQueue.Stop(); goAI = false; }
            if (goFR) { frQueue.Stop(); goFR = false; }

            // Re-initialization (settings saved in the web UI) must stop the previous Telegram polling and MQTT client,
            // otherwise two pollers fight over getUpdates (409 Conflict) and the old client keeps reconnecting.
            tgPollingCts?.Cancel();
            tgPollingCts = new CancellationTokenSource();

            if (mqttClient != null)
            {
                var oldClient = mqttClient;
                oldClient.ApplicationMessageReceivedAsync -= MqttClientApplicationMessageReceivedAsync;
                oldClient.ConnectedAsync -= MqttClientConnectedAsync;
                oldClient.DisconnectedAsync -= MqttClientDisconnectedAsync;
                try
                {
                    if (oldClient.IsConnected)
                        await oldClient.DisconnectAsync();
                }
                catch (Exception ex) { Log("app", "", "", "Error while disconnecting from mqtt server: " + ex.Message); }
                oldClient.Dispose();
                mqttClient = null;
            }

            bot = new TelegramBotClient(
                new TelegramBotClientOptions(
                    token: settings.telegram.token,
                    baseUrl: string.IsNullOrEmpty(settings.telegram.apiserver) ? null : settings.telegram.apiserver));
            bot.StartReceiving(
                updateHandler: TgHandleUpdateAsync,
                errorHandler: TgHandlePollingErrorAsync,
                receiverOptions: new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
                cancellationToken: tgPollingCts.Token);
            _ = Task.Run(() => bot.GetMe());
            _ = Task.Run(async () =>
            {
                try
                {
                    await bot.SetMyCommands(new[]
                    {
                        new BotCommand { Command = "status", Description = L10n.Tg.T("tg.cmd.status") },
                        new BotCommand { Command = "last", Description = L10n.Tg.T("tg.cmd.last") },
                        new BotCommand { Command = "stat", Description = L10n.Tg.T("tg.cmd.stat") },
                        new BotCommand { Command = "clip", Description = L10n.Tg.T("tg.cmd.clip") },
                        new BotCommand { Command = "mute", Description = L10n.Tg.T("tg.cmd.mute") },
                        new BotCommand { Command = "unmute", Description = L10n.Tg.T("tg.cmd.unmute") },
                        new BotCommand { Command = "help", Description = L10n.Tg.T("tg.cmd.help") },
                    });
                }
                catch (Exception ex) { Log("app", "", "", "Failed to set bot commands: " + ex.Message); }
            });
            Log("app", "", "", "Telegram bot polling started");

            if (settings.ai != null && !string.IsNullOrEmpty(settings.ai.url) && !string.IsNullOrEmpty(settings.ai.model))
            {
                aiQueue = new AIQueueService(bot, settings.ai);
                aiQueue.Start();
                goAI = true;
            }
            else Log("app", "", "", "AI service not configured, skipping");

            if (settings.fr != null && !string.IsNullOrEmpty(settings.fr.url) && !string.IsNullOrEmpty(settings.fr.apikey))
            {
                frQueue = new FRQueueService(bot, settings.fr);
                frQueue.Start();
                goFR = true;
            }
            else Log("app", "", "", "FR service not configured, skipping");

            try
            {
                mqttFactory = new MqttClientFactory();
                mqttClient = mqttFactory.CreateMqttClient();
                mqttOptions = new MqttClientOptionsBuilder()
                    .WithClientId("lookout")
                    .WithTcpServer(settings.mqtt.host, settings.mqtt.port)
                    .WithCredentials(settings.mqtt.user, settings.mqtt.password)
                    .WithCleanSession()
                    .Build();
                mqttClient.ApplicationMessageReceivedAsync += MqttClientApplicationMessageReceivedAsync;
                mqttClient.ConnectedAsync += MqttClientConnectedAsync;
                mqttClient.DisconnectedAsync += MqttClientDisconnectedAsync;
                await mqttClient.ConnectAsync(mqttOptions);
                Log("app", "", "", "Waiting for mqtt messages");
                var mqttSubscribeOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(settings.mqtt.eventstopic))
                    .WithTopicFilter(f => f.WithTopic(settings.mqtt.reviewstopic))
                    .Build();
                await mqttClient.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ConsoleLog("app", "", "", ex.ToString());
            }
        }

        private static async Task<T> TgCall<T>(Func<Task<T>> call, string type, string eventId, string camera)
        {
            await tgSemaphore.WaitAsync();
            try
            {
                while (true)
                {
                    try
                    {
                        var result = await call();
                        RetryQueue.MarkSent();
                        await TgAddActionsAsync(result);
                        await Task.Delay(50);
                        return result;
                    }
                    catch (ApiRequestException ex) when (ex.ErrorCode == 429)
                    {
                        int wait = (ex.Parameters?.RetryAfter ?? settings.telegram.retryonratelimit) * 1000;
                        Log(type, eventId, camera, $"Telegram rate limit, waiting {wait / 1000}s");
                        Metrics.Inc("lookout_telegram_rate_limited_total");
                        await Task.Delay(wait);
                    }
                    catch (Exception)
                    {
                        Metrics.Inc("lookout_telegram_errors_total");
                        throw;
                    }
                }
            }
            finally
            {
                tgSemaphore.Release();
            }
        }

        private static Random random = new Random();
        public static string RandomString(int length)
        {
            return new string(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyz0123456789", length).Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public static void Log(string type, string eventid, string camera, string txt)
        {
            if (settings.logger.console)
                ConsoleLog(type, eventid, camera, txt);
            if (settings.logger.file)
                FileLog(type, eventid, camera, txt);
        }

        // Recordings of an event/review (Queries.getEventQuery) with its id and camera passed as parameters.
        static SqliteCommand RecordingsCommand(SqliteConnection db, (string sql, string id, string camera) q)
        {
            var cmd = new SqliteCommand(q.sql, db);
            cmd.Parameters.AddWithValue("$id", q.id);
            cmd.Parameters.AddWithValue("$camera", q.camera);
            return cmd;
        }

        static readonly object logLock = new object();

        // Local time for log lines: UTC + options.timeoffset (the container runs in UTC).
        static DateTime LogNow => DateTime.UtcNow.AddMinutes(settings.options.timeoffset);

        // Events are handled in parallel, so writes are serialized; a failed write must not break the caller.
        public static void FileLog(string type, string eventid, string camera, string txt)
        {
            try
            {
                DateTime now = LogNow;
                lock (logLock)
                {
                    Directory.CreateDirectory("/var/log/lookout/");
                    System.IO.File.AppendAllText("/var/log/lookout/lookout_" + now.ToString("yyyy-MM-dd") + ".log",
                                                 now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + type + "\t" + eventid + "\t" + camera + "\t" + txt + "\n");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to write the log file: " + ex.Message);
            }
        }

        public static void ConsoleLog(string type, string eventid, string camera, string txt)
        {
            Console.WriteLine(LogNow.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + type + "\t" + eventid + "\t" + camera + "\t" + txt);
        }
    }
}
