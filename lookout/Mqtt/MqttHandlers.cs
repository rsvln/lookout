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
        async static Task MqttClientApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
        {
            try
            {
                var payloadText = Encoding.UTF8.GetString(arg.ApplicationMessage.Payload.ToArray());

                if (arg.ApplicationMessage.Topic == settings.mqtt.eventstopic)
                {
                    var fe = new FrigateEvent();
                    try
                    {
                        fe = JsonConvert.DeserializeObject<FrigateEvent>(payloadText, new JsonSerializerSettings
                        {
                            MissingMemberHandling = MissingMemberHandling.Ignore
                        });
                    }
                    catch
                    {
                        Log("app", "", "", "Bad payload");
                    }

                    int cami = settings.frigate.cameras.FindIndex(m => m.camera == fe.after.camera);
                    if (cami == -1) return;

                    if (settings.frigate.cameras[cami].topic != "events")
                        return;

                    if ((fe != null) && (settings.frigate.cameras.Select(x => x.camera).ToList().Contains(fe.after.camera)) && (fe.type == "end"))
                    {

                        if ((!settings.frigate.cameras[cami].snapshot) && (!settings.frigate.cameras[cami].clip) && (!settings.frigate.cameras[cami].trueend))
                            return;
                        if ((settings.frigate.cameras[cami].snapshottrigger != fe.type) && (!settings.frigate.cameras[cami].clip) && (!settings.frigate.cameras[cami].trueend))
                            return;
                        if ((settings.frigate.cameras[cami].zones.Count > 0) && (settings.frigate.cameras[cami].zones.Intersect(fe.after.entered_zones).Count() == 0))
                            return;

                        if (EventPasses(settings.frigate.cameras[cami], fe.after))
                        {
                            Log("event", fe.after.id, fe.after.camera, "Event end received");
                            Metrics.Inc("lookout_events_total", "source", "event", "type", "end");
                            _ = Task.Run(() => FrigateEventEndWorker(fe: fe));
                        }
                    }

                    if ((fe != null) && (settings.frigate.cameras.Select(x => x.camera).ToList().Contains(fe.after.camera)) && ((fe.type == "new") || (fe.type == "update")))
                    {

                        if (!settings.frigate.cameras[cami].snapshot)
                            return;
                        if (settings.frigate.cameras[cami].snapshottrigger != fe.type)
                            return;
                        if ((settings.frigate.cameras[cami].zones.Count > 0) && (settings.frigate.cameras[cami].zones.Intersect(fe.after.entered_zones).Count() == 0))
                            return;

                        if (EventPasses(settings.frigate.cameras[cami], fe.after))
                        {
                            Log("event", fe.after.id, fe.after.camera, "Event new received");
                            Metrics.Inc("lookout_events_total", "source", "event", "type", "new");
                            _ = Task.Run(() => FrigateEventNewWorker(fe: fe));
                        }
                    }
                }

                if (arg.ApplicationMessage.Topic == settings.mqtt.reviewstopic)
                {
                    var fr = new FrigateReview();
                    try
                    {
                        fr = JsonConvert.DeserializeObject<FrigateReview>(payloadText, new JsonSerializerSettings
                        {
                            MissingMemberHandling = MissingMemberHandling.Ignore
                        });
                    }
                    catch
                    {
                        Log("app", "", "", "Bad payload");
                        return;
                    }

                    int cami = settings.frigate.cameras.FindIndex(m => m.camera == fr.after.camera);
                    if (cami == -1) return;

                    if (settings.frigate.cameras[cami].topic != "reviews")
                        return;

                    if ((fr != null) && (settings.frigate.cameras.Select(x => x.camera).ToList().Contains(fr.after.camera)) && (fr.type == "end"))
                    {
                        if ((!settings.frigate.cameras[cami].snapshot) && (!settings.frigate.cameras[cami].clip) && (!settings.frigate.cameras[cami].trueend))
                            return;
                        if ((settings.frigate.cameras[cami].snapshottrigger != fr.type) && (!settings.frigate.cameras[cami].clip) && (!settings.frigate.cameras[cami].trueend))
                            return;
                        if (!settings.frigate.cameras[cami].severity.Contains(fr.after.severity))
                            return;
                        if ((settings.frigate.cameras[cami].zones.Count > 0) && (settings.frigate.cameras[cami].zones.Intersect(fr.after.data.zones).Count() == 0))
                            return;


                        if (ReviewPasses(settings.frigate.cameras[cami], fr))
                        {
                            Log("review", fr.after.id, fr.after.camera, "Review end received");
                            Metrics.Inc("lookout_events_total", "source", "review", "type", "end");
                            _ = Task.Run(() => FrigateReviewEndWorker(fr: fr));
                        }
                    }

                    if ((fr != null) && (settings.frigate.cameras.Select(x => x.camera).ToList().Contains(fr.after.camera)) && ((fr.type == "new") || (fr.type == "update")))
                    {

                        if (!settings.frigate.cameras[cami].snapshot)
                            return;
                        if (settings.frigate.cameras[cami].snapshottrigger != fr.type)
                            return;
                        if (!settings.frigate.cameras[cami].severity.Contains(fr.after.severity))
                            return;
                        if ((settings.frigate.cameras[cami].zones.Count > 0) && (settings.frigate.cameras[cami].zones.Intersect(fr.after.data.zones).Count() == 0))
                            return;

                        if (ReviewPasses(settings.frigate.cameras[cami], fr))
                        {
                            Log("review", fr.after.id, fr.after.camera, "Review new/update received");
                            Metrics.Inc("lookout_events_total", "source", "review", "type", "new");
                            _ = Task.Run(() => FrigateReviewNewWorker(fr: fr));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("mqtt", "", "", "Handler error: " + ex.Message);
            }

            return;
        }

        async static Task MqttClientConnectedAsync(MqttClientConnectedEventArgs arg)
        {
            Log("app", "", "", "Connected to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString());
            var mqttSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
                                        .WithTopicFilter(x =>
                                            {
                                                x.WithTopic(settings.mqtt.eventstopic);
                                            })
                                        .WithTopicFilter(x =>
                                            {
                                                x.WithTopic(settings.mqtt.reviewstopic);
                                            })
                                        .Build();
            await mqttClient.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);
            Log("app", "", "", "Subscribed to topics " + settings.mqtt.eventstopic + ", " + settings.mqtt.reviewstopic);
            return;
        }

        async static Task MqttClientDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
        {
            Log("app", "", "", "Disconnected from mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString());
            await Task.Delay(TimeSpan.FromSeconds(5));

            try
            {
                await mqttClient.ConnectAsync(mqttOptions);
            }
            catch
            {
                Log("app", "", "", "Reconnecting to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString() + " failed");
            }

            return;
        }

    }
}
