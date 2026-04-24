using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ObsidianX.Core.Services;

/// <summary>
/// Talks to a local Ollama server (typically <c>http://localhost:11434</c>).
/// Ollama exposes an OpenAI-compatible chat endpoint, but its native
/// <c>/api/chat</c> path is simpler and streams less awkwardly, so we
/// use that.
///
/// The Hub asks this backend to respond — Ollama runs whichever model
/// (llama3, mistral, qwen2.5, typhoon for Thai, etc.) is installed.
/// Nothing leaves the user's machine; everything stays local.
/// </summary>
public class OllamaBackend : IAiBackend
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public string Name => "ollama";

    public OllamaBackend(string baseUrl = "http://localhost:11434", HttpClient? client = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JObject.Parse(json);
            var arr = root["models"] as JArray ?? [];
            return arr.Select(m => m["name"]?.ToString() ?? "")
                      .Where(n => !string.IsNullOrEmpty(n)).ToList();
        }
        catch { return []; }
    }

    public async Task<ChatReply> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var payload = new JObject
        {
            ["model"] = request.Model,
            ["stream"] = false,
            ["messages"] = new JArray(request.Messages.Select(m => new JObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content
            })),
            ["options"] = new JObject
            {
                ["temperature"] = request.Temperature,
                ["num_predict"] = request.MaxTokens
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Ollama returned {(int)resp.StatusCode}: {body}");

        var root = JObject.Parse(body);
        var content = root["message"]?["content"]?.ToString() ?? "";
        var promptTokens = root["prompt_eval_count"]?.ToObject<int>() ?? 0;
        var completionTokens = root["eval_count"]?.ToObject<int>() ?? 0;

        return new ChatReply
        {
            Content = content,
            Model = request.Model,
            BackendName = Name,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            Elapsed = sw.Elapsed
        };
    }
}
