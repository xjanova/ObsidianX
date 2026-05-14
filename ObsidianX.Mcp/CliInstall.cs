using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ObsidianX.Core.Services;

namespace ObsidianX.Mcp;

/// <summary>
/// `obsidianx-mcp install` — one command to wire ObsidianX into Claude Code:
///   1. Seed/upgrade per-project memory rules (brain-first protocol)
///   2. Print Claude Code MCP registration command (we don't edit
///      ~/.claude.json directly — risk of corruption, Claude Code owns it)
///   3. Probe Ollama and pull required models if available + agreed
///   4. Optionally trigger initial embedding precompute
///   5. Print verification steps
///
/// Single binary that doubles as the MCP server (when invoked without args)
/// and the installer (when invoked as `obsidianx-mcp install`). Same logic
/// is callable from Client UI and from the npm wrapper, so all four
/// install surfaces share one code path.
/// </summary>
internal static class CliInstall
{
    public static void PrintTopLevelHelp()
    {
        Console.WriteLine("ObsidianX MCP — local-first brain for Claude Code");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  obsidianx-mcp                           Run as MCP server (default; spawned by Claude Code)");
        Console.WriteLine("  obsidianx-mcp <vault-path>              Run as MCP server with explicit vault path");
        Console.WriteLine("  obsidianx-mcp install [options]         Install brain-first rules + print MCP registration");
        Console.WriteLine("  obsidianx-mcp help                      Show this help");
        Console.WriteLine();
        Console.WriteLine("`install` options:");
        Console.WriteLine("  --vault PATH     Vault to install rules for (default: env OBSIDIANX_VAULT or current dir)");
        Console.WriteLine("  --pull-models    Pull nomic-embed-text + gemma3:4b via local Ollama if reachable");
        Console.WriteLine("  --precompute     Run embedding precompute after install (slow first time, ~1-2 min for 600 notes)");
        Console.WriteLine("  --quiet          Suppress section headers; print just status lines");
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts.ShowHelp) { PrintInstallHelp(); return 0; }

        var vault = ResolveVault(opts.Vault);
        Section(opts, "ObsidianX install");
        Console.WriteLine($"  Vault:     {vault}");
        Console.WriteLine($"  Rules ver: {ClaudeBrainRulesInstaller.RuleVersion}");
        Console.WriteLine();

        // Step 1 — memory rules
        Section(opts, "1/4  Brain-first memory rules");
        var ruleResult = ClaudeBrainRulesInstaller.EnsureInstalled(vault);
        Console.WriteLine($"  → {ruleResult}");
        var memDir = ComputeMemoryDir(vault);
        if (memDir != null)
        {
            Console.WriteLine($"  → Files at: {memDir}");
            foreach (var f in Directory.EnumerateFiles(memDir, "*.md"))
                Console.WriteLine($"      • {Path.GetFileName(f)}");
        }
        Console.WriteLine();

        // Step 2 — MCP registration command (we print, user runs)
        Section(opts, "2/4  MCP registration in Claude Code");
        var exePath = ResolveSelfPath();
        Console.WriteLine("  Run this once to register the MCP server with Claude Code:");
        Console.WriteLine();
        Console.WriteLine($"    claude mcp add obsidianx-brain \"{exePath}\" \"{vault}\"");
        Console.WriteLine();
        Console.WriteLine("  Or add this to your project's .mcp.json:");
        Console.WriteLine();
        var mcpJson = new JObject
        {
            ["mcpServers"] = new JObject
            {
                ["obsidianx-brain"] = new JObject
                {
                    ["command"] = exePath,
                    ["args"] = new JArray { vault },
                    ["env"] = new JObject { ["OBSIDIANX_VAULT"] = vault }
                }
            }
        };
        foreach (var line in mcpJson.ToString().Split('\n'))
            Console.WriteLine($"    {line.TrimEnd('\r')}");
        Console.WriteLine();

        // Step 3 — Ollama models (optional)
        Section(opts, "3/4  Ollama models (semantic search + LLM verification)");
        var ollamaUp = await OllamaReachable().ConfigureAwait(false);
        if (!ollamaUp)
        {
            Console.WriteLine("  ⚠  Ollama not reachable at http://localhost:11434");
            Console.WriteLine("      Install: https://ollama.com/download");
            Console.WriteLine("      Then re-run `obsidianx-mcp install --pull-models` to pull required models.");
        }
        else
        {
            var have = await ListOllamaModels().ConfigureAwait(false);
            string[] required = ["nomic-embed-text", "gemma3:4b"];
            foreach (var model in required)
            {
                var present = have.Any(m => m.StartsWith(model, StringComparison.Ordinal));
                if (present) { Console.WriteLine($"  ✓ {model}"); continue; }
                if (opts.PullModels)
                {
                    Console.WriteLine($"  ⤓ pulling {model} (this may take several minutes)...");
                    var ok = await PullOllamaModel(model).ConfigureAwait(false);
                    Console.WriteLine(ok ? $"  ✓ {model} pulled" : $"  ✗ {model} pull failed");
                }
                else
                {
                    Console.WriteLine($"  ✗ {model} (missing) — re-run with --pull-models to download");
                }
            }
        }
        Console.WriteLine();

        // Step 4 — embedding precompute (optional)
        Section(opts, "4/4  Embedding precompute");
        var embedDir = Path.Combine(vault, ".obsidianx", "embeddings");
        var existing = Directory.Exists(embedDir)
            ? Directory.EnumerateFiles(embedDir, "*.bin").Count()
            : 0;
        Console.WriteLine($"  Existing sidecars: {existing}");
        if (opts.Precompute && ollamaUp)
        {
            Console.WriteLine("  → Running precompute (only embeds notes whose sidecar is missing or stale)...");
            var graph = await TryLoadGraphAsync(vault).ConfigureAwait(false);
            if (graph != null)
            {
                var svc = new EmbeddingService();
                var written = await svc.PrecomputeMissingAsync(vault, graph).ConfigureAwait(false);
                Console.WriteLine($"  ✓ wrote {written} new embedding sidecar(s)");
            }
            else
            {
                Console.WriteLine("  ⚠  Could not load graph (no brain-export.json yet?). Open ObsidianX.Client at least once to export, then re-run.");
            }
        }
        else if (!opts.Precompute)
        {
            Console.WriteLine("  → Skipped. Re-run with --precompute to populate.");
            Console.WriteLine("    (Without embeddings, brain_semantic_search falls back to keyword.)");
        }
        Console.WriteLine();

        // Summary
        Section(opts, "Done — verification");
        Console.WriteLine("  In Claude Code, try: \"summarize ObsidianX architecture\"");
        Console.WriteLine("  Expected: Claude calls brain_search/brain_semantic_search BEFORE answering,");
        Console.WriteLine("            and cites note titles in its reply.");
        Console.WriteLine();
        Console.WriteLine("  Diagnostics:");
        Console.WriteLine("    powershell ~/.claude/scripts/brain-stats.ps1");
        Console.WriteLine();
        return 0;
    }

    // ── helpers ───────────────────────────────────────────────────────

    private record Options(string? Vault, bool PullModels, bool Precompute, bool Quiet, bool ShowHelp);

    private static Options ParseArgs(string[] args)
    {
        string? vault = null;
        bool pull = false, pre = false, quiet = false, help = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--vault" when i + 1 < args.Length: vault = args[++i]; break;
                case "--pull-models": pull = true; break;
                case "--precompute": pre = true; break;
                case "--quiet": quiet = true; break;
                case "-h" or "--help" or "help": help = true; break;
            }
        }
        return new Options(vault, pull, pre, quiet, help);
    }

    private static void PrintInstallHelp()
    {
        Console.WriteLine("Usage: obsidianx-mcp install [--vault PATH] [--pull-models] [--precompute] [--quiet]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --vault PATH      Vault to install for. Default: env OBSIDIANX_VAULT or current dir.");
        Console.WriteLine("  --pull-models     Pull nomic-embed-text + gemma3:4b via local Ollama (else just probe).");
        Console.WriteLine("  --precompute      Run embedding precompute after install.");
        Console.WriteLine("  --quiet           Suppress section headers.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  obsidianx-mcp install --vault G:\\Obsidian");
        Console.WriteLine("  obsidianx-mcp install --pull-models --precompute");
    }

    private static void Section(Options opts, string title)
    {
        if (opts.Quiet) return;
        Console.WriteLine();
        Console.WriteLine($"── {title} ──────────────────────────────");
    }

    private static string ResolveVault(string? explicitVault)
    {
        if (!string.IsNullOrWhiteSpace(explicitVault) && Directory.Exists(explicitVault))
            return Path.GetFullPath(explicitVault);
        var env = Environment.GetEnvironmentVariable("OBSIDIANX_VAULT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return Path.GetFullPath(env);
        return Path.GetFullPath(Environment.CurrentDirectory);
    }

    private static string ResolveSelfPath()
    {
        // Prefer the .exe if we have one; otherwise fall back to the dll
        // path the user can invoke as `dotnet path/to/obsidianx-mcp.dll`.
        var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(loc)) return "obsidianx-mcp.exe";
        var exe = Path.ChangeExtension(loc, ".exe");
        return File.Exists(exe) ? exe : loc;
    }

    private static string? ComputeMemoryDir(string vault)
    {
        var slug = vault.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(":", "-").Replace("\\", "-").Replace("/", "-");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(slug)) return null;
        return Path.Combine(profile, ".claude", "projects", slug, "memory");
    }

    private static async Task<bool> OllamaReachable()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync("http://localhost:11434/api/tags").ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<string[]> ListOllamaModels()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync("http://localhost:11434/api/tags").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            return ((json["models"] as JArray) ?? [])
                .Select(m => m["name"]?.ToString() ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
        }
        catch { return []; }
    }

    private static async Task<bool> PullOllamaModel(string model)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            var body = new JObject { ["name"] = model, ["stream"] = false }.ToString();
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("http://localhost:11434/api/pull", content).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<ObsidianX.Core.Models.KnowledgeGraph?> TryLoadGraphAsync(string vault)
    {
        // We only need a KnowledgeGraph for EmbeddingService.PrecomputeMissingAsync.
        // Build a minimal graph from brain-export.json (the export is a
        // post-processed snapshot — for a precompute pass it has all the
        // fields we need: id, title, file path, modified time).
        try
        {
            var exportPath = Path.Combine(vault, ".obsidianx", "brain-export.json");
            if (!File.Exists(exportPath)) return null;
            var json = await File.ReadAllTextAsync(exportPath).ConfigureAwait(false);
            var root = JObject.Parse(json);
            var nodes = (root["Nodes"] as JArray) ?? [];
            var graph = new ObsidianX.Core.Models.KnowledgeGraph();
            foreach (var n in nodes)
            {
                var rel = n["RelativePath"]?.ToString() ?? "";
                var node = new ObsidianX.Core.Models.KnowledgeNode
                {
                    Id = n["Id"]?.ToString() ?? "",
                    Title = n["Title"]?.ToString() ?? "",
                    FilePath = string.IsNullOrEmpty(rel) ? "" : Path.Combine(vault, rel),
                    ModifiedAt = n["ModifiedAt"] != null
                        ? DateTime.Parse(n["ModifiedAt"]!.ToString()).ToUniversalTime()
                        : DateTime.MinValue
                };
                graph.Nodes.Add(node);
            }
            return graph;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠  graph load failed: {ex.Message}");
            return null;
        }
    }
}
