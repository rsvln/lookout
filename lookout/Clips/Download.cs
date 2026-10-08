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
        async static Task DownloadFileAsync(string url, string filename)
        {
            try
            {
                using var http = new HttpClient();
                var data = await http.GetByteArrayAsync(url);
                await System.IO.File.WriteAllBytesAsync(filename, data);
            }
            catch (Exception)
            {
                Log("app", "", "", "Failed to download File: " + url);
            }
        }

    }
}
