using System.Net;
using System.Net.Http;
using Xunit;

namespace Lookout.Tests
{
    public class NotifierAndAiTests
    {
        class QueueHandler : HttpMessageHandler
        {
            public List<(HttpMethod method, string url, string body, string auth)> Calls = new();
            public Queue<HttpResponseMessage> Replies = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                Calls.Add((request.Method, request.RequestUri.ToString(), body, request.Headers.Authorization?.ToString()));
                return Replies.Count > 0
                    ? Replies.Dequeue()
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
        }

        static NotifyMessage Text(string body = "person at the door") => new NotifyMessage
        {
            Kind = "event", Id = "e1", Camera = "front", Body = body
        };

        static HttpClient Client(QueueHandler h) => new HttpClient(h) { Timeout = TimeSpan.FromSeconds(5) };

        [Fact]
        public async Task Ntfy_PostsTitleAndBody()
        {
            var h = new QueueHandler();
            var n = new NtfyNotifier(new NotifierSettings { type = "ntfy", url = "https://ntfy.sh/alerts" }, Client(h));
            await n.SendAsync(Text());
            var call = Assert.Single(h.Calls);
            Assert.Equal("https://ntfy.sh/alerts/", call.url.TrimEnd('/') + "/");
            Assert.Contains("person at the door", call.body);
        }

        [Fact]
        public async Task Discord_PostsJsonContent()
        {
            var h = new QueueHandler();
            var n = new DiscordNotifier(new NotifierSettings { type = "discord", url = "https://discord.example/hook" }, Client(h));
            await n.SendAsync(Text());
            Assert.Contains("\"content\":\"person at the door\"", Assert.Single(h.Calls).body);
        }

        [Fact]
        public async Task Webhook_PostsKindCameraAndBody()
        {
            var h = new QueueHandler();
            var n = new WebhookNotifier(new NotifierSettings { type = "webhook", url = "http://127.0.0.1/hook" }, Client(h));
            await n.SendAsync(Text());
            string body = Assert.Single(h.Calls).body;
            Assert.Contains("\"kind\":\"event\"", body);
            Assert.Contains("\"camera\":\"front\"", body);
            Assert.Contains("\"id\":\"e1\"", body);
        }

        [Fact]
        public async Task Matrix_UploadsThenSendsImage()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "lookout-mtx-" + Guid.NewGuid().ToString("N") + ".jpg");
            File.WriteAllBytes(tmp, new byte[] { 0xff, 0xd8, 0xff });
            try
            {
                var h = new QueueHandler();
                h.Replies.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"content_uri\":\"mxc://s/abc\"}") });
                h.Replies.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
                var n = new MatrixNotifier(new NotifierSettings { type = "matrix", homeserver = "https://matrix.example", token = "syt", room = "!r:example" }, Client(h));
                await n.SendAsync(new NotifyMessage { Body = "hi", SnapshotPaths = new List<string> { tmp } });
                Assert.Equal(2, h.Calls.Count);
                Assert.Contains("/_matrix/media/v3/upload", h.Calls[0].url);
                Assert.Contains("/_matrix/client/v3/rooms/", h.Calls[1].url);
                Assert.Contains("mxc://s/abc", h.Calls[1].body);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [Fact]
        public void Hub_Create_SkipsUnknownAndIncomplete()
        {
            var http = Client(new QueueHandler());
            Assert.Null(NotifierHub.Create(new NotifierSettings { type = "fax" }, http));
            Assert.Null(NotifierHub.Create(new NotifierSettings { type = "ntfy" }, http));
            Assert.Equal("ntfy:https://ntfy.sh/x", NotifierHub.Create(new NotifierSettings { type = "ntfy", url = "https://ntfy.sh/x" }, http).Name);
        }

        [Theory]
        [InlineData("hello", "hello")]
        [InlineData("<think>scratch</think>\nA person.", "A person.")]
        [InlineData("before <think>x", "before")]
        public void StripThinking(string raw, string expected) => Assert.Equal(expected, AiProviders.StripThinking(raw));

        [Fact]
        public async Task Ollama_UsesGenerateAndNoThink()
        {
            var h = new QueueHandler();
            h.Replies.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"response\":\"a dog\"}") });
            var p = new OllamaProvider(new AISettings { url = "http://ollama", model = "llava", thinking = false }, Client(h));
            Assert.Equal("a dog", await p.DescribeAsync(new byte[] { 1, 2 }, "what is this"));
            Assert.Contains("/api/generate", h.Calls[0].url);
            Assert.Contains("/no_think", h.Calls[0].body);
        }

        [Fact]
        public async Task OpenAi_PostsChatCompletionsWithBearer()
        {
            var h = new QueueHandler();
            h.Replies.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"car on the street\"}}]}")
            });
            var p = new OpenAiProvider(new AISettings { url = "http://llm", model = "gpt-4o-mini", apikey = "sk-x" }, Client(h));
            Assert.Equal("car on the street", await p.DescribeAsync(new byte[] { 1 }, "describe"));
            Assert.Contains("/v1/chat/completions", h.Calls[0].url);
            Assert.StartsWith("Bearer sk-x", h.Calls[0].auth);
        }

        [Fact]
        public async Task Gemini_ReadsCandidateText()
        {
            var h = new QueueHandler();
            h.Replies.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"two people\"}]}}]}")
            });
            var p = new GeminiProvider(new AISettings { url = "https://generativelanguage.googleapis.com", model = "gemini-flash", apikey = "k" }, Client(h));
            Assert.Equal("two people", await p.DescribeAsync(new byte[] { 1 }, "describe"));
            Assert.Contains(":generateContent", h.Calls[0].url);
            Assert.Contains("key=k", h.Calls[0].url);
        }

        [Fact]
        public void Factory_PicksProvider()
        {
            var http = Client(new QueueHandler());
            Assert.Equal("ollama", AiProviders.Create(new AISettings { url = "http://x", model = "m" }, http).Name);
            Assert.Equal("openai", AiProviders.Create(new AISettings { provider = "openai", url = "http://x", model = "m" }, http).Name);
            Assert.Equal("gemini", AiProviders.Create(new AISettings { provider = "Gemini", url = "http://x", model = "m" }, http).Name);
        }
    }
}
