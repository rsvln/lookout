using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Lookout
{
    public interface IAiProvider
    {
        string Name { get; }
        Task<string> DescribeAsync(byte[] imageJpeg, string prompt, CancellationToken ct = default);
    }

    public static class AiProviders
    {
        public static IAiProvider Create(AISettings s, HttpClient http)
        {
            string p = (s?.provider ?? "ollama").Trim().ToLowerInvariant();
            return p switch
            {
                "openai" or "openai-compatible" => new OpenAiProvider(s, http),
                "gemini" => new GeminiProvider(s, http),
                _ => new OllamaProvider(s, http)
            };
        }

        internal static string StripThinking(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = text.Trim();
            int close = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (close >= 0) return text.Substring(close + 8).Trim();
            int open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            return open >= 0 ? text.Substring(0, open).Trim() : text;
        }
    }

    public class OllamaProvider : IAiProvider
    {
        readonly AISettings s;
        readonly HttpClient http;
        public string Name => "ollama";

        public OllamaProvider(AISettings s, HttpClient http) { this.s = s; this.http = http; }

        public async Task<string> DescribeAsync(byte[] imageJpeg, string prompt, CancellationToken ct = default)
        {
            var requestBody = new
            {
                model = s.model,
                prompt = s.thinking ? prompt : "/no_think " + prompt,
                images = new[] { Convert.ToBase64String(imageJpeg) },
                stream = false,
                options = new { num_predict = s.numpredict, temperature = s.temperature }
            };
            using var response = await http.PostAsync(s.url.TrimEnd('/') + "/api/generate",
                new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"), ct);
            response.EnsureSuccessStatusCode();
            var parsed = JsonSerializer.Deserialize<OllamaResponse>(await response.Content.ReadAsStringAsync(ct));
            return AiProviders.StripThinking((!string.IsNullOrEmpty(parsed?.response) ? parsed.response : parsed?.thinking)?.Trim());
        }
    }

    public class OpenAiProvider : IAiProvider
    {
        readonly AISettings s;
        readonly HttpClient http;
        public string Name => "openai";

        public OpenAiProvider(AISettings s, HttpClient http) { this.s = s; this.http = http; }

        public async Task<string> DescribeAsync(byte[] imageJpeg, string prompt, CancellationToken ct = default)
        {
            var body = new
            {
                model = s.model,
                max_tokens = s.numpredict,
                temperature = s.temperature,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(imageJpeg) } }
                        }
                    }
                }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, s.url.TrimEnd('/') + "/v1/chat/completions");
            if (!string.IsNullOrEmpty(s.apikey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.apikey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            string text = content.ValueKind == JsonValueKind.String ? content.GetString() : content.ToString();
            return AiProviders.StripThinking(text);
        }
    }

    public class GeminiProvider : IAiProvider
    {
        readonly AISettings s;
        readonly HttpClient http;
        public string Name => "gemini";

        public GeminiProvider(AISettings s, HttpClient http) { this.s = s; this.http = http; }

        public async Task<string> DescribeAsync(byte[] imageJpeg, string prompt, CancellationToken ct = default)
        {
            string root = string.IsNullOrEmpty(s.url) ? "https://generativelanguage.googleapis.com" : s.url.TrimEnd('/');
            string url = root + "/v1beta/models/" + Uri.EscapeDataString(s.model) + ":generateContent";
            if (!string.IsNullOrEmpty(s.apikey))
                url += (url.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(s.apikey);
            var body = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new { inline_data = new { mime_type = "image/jpeg", data = Convert.ToBase64String(imageJpeg) } }
                        }
                    }
                },
                generationConfig = new { maxOutputTokens = s.numpredict, temperature = s.temperature }
            };
            using var response = await http.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            string text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            return AiProviders.StripThinking(text);
        }
    }
}
