using System.Text;
using Newtonsoft.Json;

namespace ObsidianX.Core.Services;

/// <summary>
/// Orchestrator: takes a plain chat request, enriches it with brain
/// context (pulled from brain-export.json), forwards to the chosen
/// backend, and returns the reply. Any client that can hit our REST
/// API — our own WPF app, a cURL script, Open WebUI, a custom mobile
/// app — gets a brain-aware model response without knowing anything
/// about Claude / Ollama / embeddings.
///
/// The brain context is built with two passes:
///   1. Quick keyword search over brain-export.json using the user's
///      last message.
///   2. Top-N hits' titles + previews are summarised and prepended as
///      a system message so the model grounds its reply on what's
///      actually in the user's vault.
/// No external embeddings — we lean on the existing SQLite FTS5 /
/// brain-export preview so responses stay instant.
/// </summary>
public class AiHubService
{
    private readonly string _vaultPath;
    private readonly Dictionary<string, IAiBackend> _backends = [];
    public string DefaultBackend { get; set; } = "ollama";
    public string DefaultModel { get; set; } = "llama3.2";
    /// <summary>Pull this many notes as brain context per request.</summary>
    public int ContextNoteCount { get; set; } = 5;

    public AiHubService(string vaultPath)
    {
        _vaultPath = vaultPath;
    }

    public void Register(IAiBackend backend) => _backends[backend.Name] = backend;

    public IReadOnlyDictionary<string, IAiBackend> Backends => _backends;

    public async Task<ChatReply> ChatAsync(
        string userMessage,
        string? backendName = null,
        string? model = null,
        List<ChatMessage>? history = null,
        CancellationToken ct = default)
    {
        backendName ??= DefaultBackend;
        model ??= DefaultModel;

        if (!_backends.TryGetValue(backendName, out var backend))
            throw new InvalidOperationException($"no backend registered: {backendName}");

        var (contextText, noteIds) = BuildBrainContext(userMessage);

        var messages = new List<ChatMessage>();
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = BuildSystemPrompt(contextText)
        });
        if (history != null) messages.AddRange(history);
        messages.Add(new ChatMessage { Role = "user", Content = userMessage });

        var reply = await backend.ChatAsync(new ChatRequest
        {
            Model = model,
            Messages = messages,
            Temperature = 0.7,
            MaxTokens = 2048
        }, ct);

        reply.ContextNoteIds = noteIds;
        return reply;
    }

    private string BuildSystemPrompt(string brainContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an AI assistant with access to the user's personal knowledge brain (ObsidianX).");
        sb.AppendLine("Prefer answers grounded in the owner's notes when relevant; cite note titles in parens.");
        sb.AppendLine("If the user's question isn't covered, say so and answer from general knowledge.");
        if (!string.IsNullOrWhiteSpace(brainContext))
        {
            sb.AppendLine();
            sb.AppendLine("## Relevant notes from the brain:");
            sb.Append(brainContext);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Load brain-export.json and score nodes against the user's message
    /// by simple keyword overlap. Return a concatenated block of top-N
    /// previews plus the matched node ids (for logging / pulse).
    /// </summary>
    private (string text, List<string> noteIds) BuildBrainContext(string userMessage)
    {
        var exportPath = Path.Combine(_vaultPath, ".obsidianx", "brain-export.json");
        if (!File.Exists(exportPath)) return ("", []);

        List<BrainNodeLite>? nodes;
        try
        {
            var json = File.ReadAllText(exportPath);
            var root = JsonConvert.DeserializeObject<BrainExportLite>(json);
            nodes = root?.Nodes;
        }
        catch { return ("", []); }
        if (nodes == null || nodes.Count == 0) return ("", []);

        // Simple term-frequency scoring (lowercased, non-alnum tokenization).
        // It's not SQLite FTS5, but it doesn't need a DB and handles the Thai
        // substring match fine for short queries.
        var terms = Tokenize(userMessage.ToLowerInvariant())
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToList();
        if (terms.Count == 0) return ("", []);

        var scored = new List<(BrainNodeLite node, double score)>();
        foreach (var n in nodes)
        {
            var blob = ((n.Title ?? "") + " " + (n.Preview ?? "") + " " +
                        string.Join(" ", n.Tags ?? [])).ToLowerInvariant();
            var titleLower = (n.Title ?? "").ToLowerInvariant();
            double s = 0;
            foreach (var t in terms)
            {
                if (blob.Contains(t)) s += 1;
                if (titleLower.Contains(t)) s += 2;   // title match worth more
            }
            if (s > 0) scored.Add((n, s));
        }
        scored = scored.OrderByDescending(x => x.score).Take(ContextNoteCount).ToList();

        var sb = new StringBuilder();
        var ids = new List<string>();
        foreach (var pair in scored)
        {
            var n = pair.node;
            sb.AppendLine();
            sb.AppendLine($"### {n.Title}");
            if (!string.IsNullOrEmpty(n.PrimaryCategory)) sb.AppendLine($"Category: {n.PrimaryCategory}");
            if (!string.IsNullOrEmpty(n.RelativePath)) sb.AppendLine($"Path: {n.RelativePath}");
            if (!string.IsNullOrEmpty(n.Preview))
            {
                sb.AppendLine();
                sb.AppendLine(n.Preview);
            }
            if (!string.IsNullOrEmpty(n.Id)) ids.Add(n.Id);
        }
        return (sb.ToString(), ids);
    }

    private static IEnumerable<string> Tokenize(string s)
    {
        var buf = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || (ch >= '\u0E00' && ch <= '\u0E7F'))
                buf.Append(ch);
            else if (buf.Length > 0) { yield return buf.ToString(); buf.Clear(); }
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    // Minimal subset matching BrainExporter's schema — avoids importing
    // the whole BrainExporter model into this file.
    private class BrainExportLite { public List<BrainNodeLite>? Nodes { get; set; } }
    private class BrainNodeLite
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Preview { get; set; }
        public string? PrimaryCategory { get; set; }
        public string? RelativePath { get; set; }
        public List<string>? Tags { get; set; }
    }
}
