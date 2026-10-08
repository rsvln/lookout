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
        // AI prompt for a snapshot: the config's prompt, or the AI language's default one. "Answer in <language>" is added
        // only when options.locale.ai is set explicitly, so a prompt written in its own language keeps working as before.
        static string AiPrompt(bool person)
        {
            string prompt = person ? settings.ai?.humanprompt : settings.ai?.nonhumanprompt;
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = L10n.Ai.T(person ? "ai.prompt.human" : "ai.prompt.nonhuman");
            prompt = prompt.Trim();
            return settings.options?.locale?.aiExplicit == true ? prompt + " " + L10n.Ai.T("ai.reply_language") : prompt;
        }

    }
}
