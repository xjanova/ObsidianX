using ObsidianX.Server.Hubs;
using ObsidianX.Core.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ASCII art banner
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine(@"
   ____  _         _     _ _             __  __
  / __ \| |__  ___(_) __| (_) __ _ _ __ \ \/ /
 | |  | | '_ \/ __| |/ _` | |/ _` | '_ \ \  /
 | |__| | |_) \__ \ | (_| | | (_| | | | |/  \
  \____/|_.__/|___/_|\__,_|_|\__,_|_| |_/_/\_\

  ╔══════════════════════════════════════════════╗
  ║   ObsidianX Server — Brain Matchmaking Hub   ║
  ║   Neural Knowledge Network v2.0.0             ║
  ╚══════════════════════════════════════════════╝
");
Console.ResetColor();

// REST API endpoints
app.MapGet("/api/health", () => new
{
    Status = "Healthy",
    Timestamp = DateTime.UtcNow,
    Uptime = Environment.TickCount64 / 1000
});

app.MapGet("/api/peers", () =>
{
    return BrainHub.GetPeersSnapshot();
});

app.MapGet("/api/stats", () =>
{
    return BrainHub.GetStatsSnapshot();
});

// ─────────────── Brain Export endpoints ───────────────
// These read the local vault's brain-export.json, so external tools
// (Claude, other apps) can fetch the current owner's expertise over HTTP
// without needing filesystem access.
//
// Configure via ObsidianX__VaultPath environment variable.
static string ResolveVaultPath()
{
    var v = Environment.GetEnvironmentVariable("ObsidianX__VaultPath");
    if (!string.IsNullOrWhiteSpace(v)) return v;
    return @"G:\Obsidian";
}

static BrainExport? LoadExport()
{
    var path = Path.Combine(ResolveVaultPath(), ".obsidianx", "brain-export.json");
    if (!File.Exists(path)) return null;
    try
    {
        return Newtonsoft.Json.JsonConvert.DeserializeObject<BrainExport>(File.ReadAllText(path));
    }
    catch { return null; }
}

app.MapGet("/api/brain/export", () =>
{
    var export = LoadExport();
    return export is null
        ? Results.NotFound(new { error = "brain-export.json not found — run Export Brain in ObsidianX Settings" })
        : Results.Ok(export);
});

app.MapGet("/api/brain/manifest", () =>
{
    var path = Path.Combine(ResolveVaultPath(), ".obsidianx", "brain-manifest.json");
    if (!File.Exists(path))
        return Results.NotFound(new { error = "brain-manifest.json not found" });
    return Results.Content(File.ReadAllText(path), "application/json");
});

app.MapGet("/api/brain/expertise", () =>
{
    var export = LoadExport();
    if (export is null) return Results.NotFound(new { error = "no export" });
    return Results.Ok(new
    {
        export.BrainAddress,
        export.DisplayName,
        export.GeneratedAt,
        export.Expertise
    });
});

app.MapGet("/api/brain/search", (string q, int? limit) =>
{
    var export = LoadExport();
    if (export is null) return Results.NotFound(new { error = "no export" });
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q is required" });

    var max = limit.GetValueOrDefault(25);
    var ql = q.ToLowerInvariant();

    var matches = export.Nodes.Select(n => new
    {
        Node = n,
        Score = Score(n, ql)
    })
    .Where(x => x.Score > 0)
    .OrderByDescending(x => x.Score)
    .Take(max)
    .Select(x => new { x.Score, x.Node.Title, x.Node.RelativePath,
        x.Node.PrimaryCategory, x.Node.Tags, x.Node.Preview })
    .ToList();

    return Results.Ok(new { query = q, count = matches.Count, results = matches });

    static double Score(NodeSummary n, string ql)
    {
        double s = 0;
        if (n.Title.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 3;
        if (n.Tags.Any(t => t.Contains(ql, StringComparison.OrdinalIgnoreCase))) s += 2;
        if (n.Preview.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1;
        if (n.PrimaryCategory.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1.5;
        return s;
    }
});

app.MapGet("/api/brain/note/{id}", (string id) =>
{
    var export = LoadExport();
    if (export is null) return Results.NotFound(new { error = "no export" });

    var node = export.Nodes.FirstOrDefault(n => n.Id == id);
    if (node is null) return Results.NotFound(new { error = "node not found" });

    var full = Path.Combine(export.VaultPath, node.RelativePath);
    var content = File.Exists(full) ? File.ReadAllText(full) : node.Preview;

    return Results.Ok(new
    {
        node.Id, node.Title, node.RelativePath, node.PrimaryCategory,
        node.SecondaryCategories, node.Tags, node.WordCount,
        node.ModifiedAt, node.LinkedNodeIds, Content = content
    });
});

// ─────────────── AI Hub endpoints ───────────────
// Lets any client (our WPF app, cURL, Open WebUI, a mobile app,
// custom scripts) query a local LLM backend with the brain's
// context automatically attached. Currently wraps Ollama; more
// backends come online as adapters are added.
static AiHubService BuildHub()
{
    var hub = new AiHubService(ResolveVaultPath());
    var ollamaUrl = Environment.GetEnvironmentVariable("OBSIDIANX_OLLAMA_URL")
                 ?? "http://localhost:11434";
    hub.Register(new OllamaBackend(ollamaUrl));
    hub.DefaultModel = Environment.GetEnvironmentVariable("OBSIDIANX_DEFAULT_MODEL")
                    ?? "llama3.2";
    return hub;
}

app.MapGet("/api/ai/backends", async () =>
{
    var hub = BuildHub();
    var list = new List<object>();
    foreach (var (name, be) in hub.Backends)
    {
        var available = await be.IsAvailableAsync();
        var models = available ? await be.ListModelsAsync() : [];
        list.Add(new { name, available, models });
    }
    return Results.Ok(new
    {
        defaultBackend = hub.DefaultBackend,
        defaultModel = hub.DefaultModel,
        backends = list
    });
});

app.MapPost("/api/ai/chat", async (AiChatRequest req) =>
{
    try
    {
        var hub = BuildHub();
        var reply = await hub.ChatAsync(
            req.Message,
            backendName: req.Backend,
            model: req.Model,
            history: req.History);
        return Results.Ok(new
        {
            reply = reply.Content,
            model = reply.Model,
            backend = reply.BackendName,
            elapsed_ms = reply.Elapsed.TotalMilliseconds,
            tokens = new { prompt = reply.PromptTokens, completion = reply.CompletionTokens },
            context_notes = reply.ContextNoteIds
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message, type = ex.GetType().Name });
    }
});

// SignalR hub for real-time brain connections
app.MapHub<BrainHub>("/brain-hub");

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("  [OK] Server ready at http://localhost:5142");
Console.WriteLine("  [OK] SignalR hub at http://localhost:5142/brain-hub");
Console.WriteLine("  [OK] Waiting for brains to connect...\n");
Console.ResetColor();

app.Run("http://0.0.0.0:5142");


public record AiChatRequest(
    string Message,
    string? Backend = null,
    string? Model = null,
    List<ChatMessage>? History = null
);
