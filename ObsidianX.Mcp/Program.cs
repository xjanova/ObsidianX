using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ObsidianX.Core.Services;

// ─────────────────────────────────────────────────────────────────────────
// ObsidianX MCP Server (stdio, JSON-RPC 2.0)
//
// Exposes the local brain-export.json to Claude Code CLI (and any MCP
// client) as tools: brain_search, brain_get_note, brain_expertise,
// brain_list, brain_stats, brain_import_path.
//
// Transport: stdio (one JSON-RPC message per line).
// Vault location: OBSIDIANX_VAULT env var, or first CLI arg, or default.
// ─────────────────────────────────────────────────────────────────────────

namespace ObsidianX.Mcp;

internal static class Program
{
    private const string ProtocolVersion = "2025-06-18";
    private const string ServerName = "obsidianx-brain";
    private const string ServerVersion = "1.0.0";

    private static string _vaultPath = ResolveVault(Environment.GetCommandLineArgs());

    public static async Task<int> Main(string[] args)
    {
        // stdin/stdout must be UTF-8, no BOM; stderr is free for logs.
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        Log($"Starting MCP server · vault={_vaultPath}");

        var reader = Console.In;
        var writer = Console.Out;

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string response;
            try
            {
                response = Handle(line);
            }
            catch (Exception ex)
            {
                Log($"handler error: {ex}");
                response = BuildError(null, -32603, $"internal error: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(response))
            {
                await writer.WriteLineAsync(response);
                await writer.FlushAsync();
            }
        }
        return 0;
    }

    private static string Handle(string line)
    {
        var req = JObject.Parse(line);
        var id = req["id"];
        var method = req["method"]?.ToString();
        var parameters = req["params"] as JObject;

        return method switch
        {
            "initialize"      => Initialize(id),
            "initialized"     => "", // notification, no response
            "notifications/initialized" => "",
            "tools/list"      => ToolsList(id),
            "tools/call"      => ToolsCall(id, parameters),
            "resources/list"  => ResourcesList(id),
            "resources/read"  => ResourcesRead(id, parameters),
            "ping"            => BuildResult(id, new JObject()),
            _                 => BuildError(id, -32601, $"method not found: {method}")
        };
    }

    // ───────────── initialize ─────────────

    private static string Initialize(JToken? id) => BuildResult(id, new JObject
    {
        ["protocolVersion"] = ProtocolVersion,
        ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = ServerVersion },
        ["capabilities"] = new JObject
        {
            ["tools"] = new JObject(),
            ["resources"] = new JObject()
        },
        ["instructions"] =
            "This is xman's personal brain (ObsidianX). Use brain_search to find notes by keyword, " +
            "brain_expertise to see what domains the owner knows deeply, and brain_get_note " +
            "to fetch the full content of a specific note by id. Always prefer citing the owner's " +
            "notes over generic answers when they are relevant."
    });

    // ───────────── tools/list ─────────────

    private static string ToolsList(JToken? id) => BuildResult(id, new JObject
    {
        ["tools"] = new JArray
        {
            Tool("brain_search",
                "Full-text search across brain notes. Returns top matches with title, category, tags, and preview.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["query"] = new JObject { ["type"] = "string", ["description"] = "search keyword or phrase" },
                        ["limit"] = new JObject { ["type"] = "integer", ["description"] = "max results (default 10)", ["default"] = 10 }
                    },
                    ["required"] = new JArray { "query" }
                }),
            Tool("brain_get_note",
                "Fetch the full content of a single note by its id (from brain_search / brain_list).",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["id"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray { "id" }
                }),
            Tool("brain_expertise",
                "List the owner's knowledge domains ranked by depth. Returns category, score (0-1), note count, word count.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
            Tool("brain_list",
                "List notes, optionally filtered by category or tag. Returns id, title, category, path.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["category"] = new JObject { ["type"] = "string", ["description"] = "optional category filter (e.g. Programming)" },
                        ["tag"] = new JObject { ["type"] = "string", ["description"] = "optional tag filter" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 50 }
                    }
                }),
            Tool("brain_stats",
                "High-level stats: brain name, address, note/word counts, top tags, top categories.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
            Tool("brain_import_path",
                "Run Resonance Scan on a filesystem path and import matching notes into the brain. " +
                "Use this when the user asks to 'import from X' or 'scan folder Y'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "absolute folder path to scan" },
                        ["patterns"] = new JObject { ["type"] = "string", ["description"] = "semicolon-separated patterns, default CLAUDE.md;README.md;*.md" },
                        ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Reference", "Copy" }, ["default"] = "Reference" }
                    },
                    ["required"] = new JArray { "path" }
                })
        }
    });

    private static JObject Tool(string name, string description, JObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema
    };

    // ───────────── tools/call dispatch ─────────────

    private static string ToolsCall(JToken? id, JObject? parameters)
    {
        var name = parameters?["name"]?.ToString();
        var args = parameters?["arguments"] as JObject ?? new JObject();

        try
        {
            JToken result = name switch
            {
                "brain_search"      => BrainSearch(args),
                "brain_get_note"    => BrainGetNote(args),
                "brain_expertise"   => BrainExpertise(),
                "brain_list"        => BrainList(args),
                "brain_stats"       => BrainStats(),
                "brain_import_path" => BrainImportPath(args),
                _ => throw new InvalidOperationException($"unknown tool: {name}")
            };

            return BuildResult(id, new JObject
            {
                ["content"] = new JArray { new JObject
                {
                    ["type"] = "text",
                    ["text"] = result.ToString(Formatting.Indented)
                }}
            });
        }
        catch (Exception ex)
        {
            return BuildResult(id, new JObject
            {
                ["isError"] = true,
                ["content"] = new JArray { new JObject
                {
                    ["type"] = "text",
                    ["text"] = $"Error: {ex.Message}"
                }}
            });
        }
    }

    // ───────────── Tools ─────────────

    private static JToken BrainSearch(JObject args)
    {
        var query = args["query"]?.ToString() ?? "";
        var limit = args["limit"]?.ToObject<int>() ?? 10;
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");

        var export = LoadExport()
            ?? throw new InvalidOperationException("brain-export.json not found — open ObsidianX → Settings → Export Brain Now");

        var ql = query.ToLowerInvariant();
        var matches = export.Nodes.Select(n => new
        {
            Node = n,
            Score = ScoreNode(n, ql)
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Take(limit)
        .ToList();

        // Log access for each hit so the 3D graph can pulse the matching nodes
        foreach (var m in matches) LogAccess(m.Node.Id, "search", query);

        return new JObject
        {
            ["query"] = query,
            ["count"] = matches.Count,
            ["results"] = new JArray(matches.Select(x => new JObject
            {
                ["id"] = x.Node.Id,
                ["title"] = x.Node.Title,
                ["score"] = x.Score,
                ["category"] = x.Node.PrimaryCategory,
                ["tags"] = new JArray(x.Node.Tags),
                ["path"] = x.Node.RelativePath,
                ["preview"] = x.Node.Preview
            }))
        };
    }

    private static JToken BrainGetNote(JObject args)
    {
        var nodeId = args["id"]?.ToString() ?? throw new ArgumentException("id is required");
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        var node = export.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException($"note not found: {nodeId}");

        var fullPath = Path.Combine(export.VaultPath, node.RelativePath);
        var content = File.Exists(fullPath) ? File.ReadAllText(fullPath) : node.Preview;
        LogAccess(node.Id, "get_note", node.Title);
        return new JObject
        {
            ["id"] = node.Id,
            ["title"] = node.Title,
            ["path"] = node.RelativePath,
            ["category"] = node.PrimaryCategory,
            ["tags"] = new JArray(node.Tags),
            ["wordCount"] = node.WordCount,
            ["modifiedAt"] = node.ModifiedAt,
            ["content"] = content
        };
    }

    private static JToken BrainExpertise()
    {
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        return new JObject
        {
            ["brainAddress"] = export.BrainAddress,
            ["displayName"] = export.DisplayName,
            ["expertise"] = new JArray(export.Expertise.Select(e => new JObject
            {
                ["category"] = e.Category,
                ["score"] = e.Score,
                ["noteCount"] = e.NoteCount,
                ["totalWords"] = e.TotalWords,
                ["growthRate"] = e.GrowthRate,
                ["lastUpdated"] = e.LastUpdated
            }))
        };
    }

    private static JToken BrainList(JObject args)
    {
        var category = args["category"]?.ToString();
        var tag = args["tag"]?.ToString();
        var limit = args["limit"]?.ToObject<int>() ?? 50;

        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        IEnumerable<NodeSummary> q = export.Nodes;
        if (!string.IsNullOrEmpty(category))
            q = q.Where(n => n.PrimaryCategory.Equals(category, StringComparison.OrdinalIgnoreCase)
                          || n.SecondaryCategories.Any(c => c.Equals(category, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrEmpty(tag))
            q = q.Where(n => n.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));

        return new JArray(q.Take(limit).Select(n => new JObject
        {
            ["id"] = n.Id,
            ["title"] = n.Title,
            ["category"] = n.PrimaryCategory,
            ["tags"] = new JArray(n.Tags),
            ["path"] = n.RelativePath,
            ["wordCount"] = n.WordCount
        }));
    }

    private static JToken BrainStats()
    {
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        return new JObject
        {
            ["brainAddress"] = export.BrainAddress,
            ["displayName"] = export.DisplayName,
            ["generatedAt"] = export.GeneratedAt,
            ["totalNotes"] = export.TotalNotes,
            ["totalWords"] = export.TotalWords,
            ["totalEdges"] = export.TotalEdges,
            ["topCategories"] = new JArray(export.Expertise.Take(5).Select(e => new JObject
            {
                ["category"] = e.Category,
                ["score"] = e.Score,
                ["noteCount"] = e.NoteCount
            })),
            ["topTags"] = new JArray(export.TopTags.Take(10).Select(t => new JObject
            {
                ["tag"] = t.Tag,
                ["count"] = t.Count
            }))
        };
    }

    private static JToken BrainImportPath(JObject args)
    {
        var path = args["path"]?.ToString() ?? throw new ArgumentException("path is required");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

        var patterns = args["patterns"]?.ToString() ?? "CLAUDE.md;README.md;*.md";
        var modeStr = args["mode"]?.ToString() ?? "Reference";
        if (!Enum.TryParse<VaultImporter.ImportMode>(modeStr, true, out var mode))
            mode = VaultImporter.ImportMode.Reference;

        var importer = new VaultImporter();
        var opts = new ImportOptions
        {
            VaultPath = _vaultPath,
            ScanPaths = [path],
            Patterns = patterns,
            Mode = mode
        };

        var report = importer.Scan(opts);
        var result = importer.Import(report.Hits, opts);

        return new JObject
        {
            ["scanned"] = report.Hits.Count,
            ["imported"] = result.Imported.Count,
            ["skipped"] = result.Skipped.Count,
            ["errors"] = new JArray(result.Errors),
            ["visitedFolders"] = report.VisitedFolders,
            ["prunedFolders"] = report.PrunedFolders,
            ["nearDuplicates"] = report.NearDuplicatesSkipped,
            ["note"] = "Run 'Export Brain Now' in ObsidianX UI to refresh brain-export.json after import."
        };
    }

    // ───────────── resources ─────────────

    private static string ResourcesList(JToken? id) => BuildResult(id, new JObject
    {
        ["resources"] = new JArray
        {
            new JObject
            {
                ["uri"] = "obsidianx://brain/export",
                ["name"] = "Brain Export (JSON)",
                ["description"] = "Full machine-readable index of the brain",
                ["mimeType"] = "application/json"
            },
            new JObject
            {
                ["uri"] = "obsidianx://brain/card",
                ["name"] = "Brain Card (Markdown)",
                ["description"] = "Human-readable summary of expertise and top notes",
                ["mimeType"] = "text/markdown"
            }
        }
    });

    private static string ResourcesRead(JToken? id, JObject? parameters)
    {
        var uri = parameters?["uri"]?.ToString() ?? "";
        var file = uri switch
        {
            "obsidianx://brain/export" => Path.Combine(_vaultPath, ".obsidianx", "brain-export.json"),
            "obsidianx://brain/card"   => Path.Combine(_vaultPath, ".obsidianx", "brain-export.md"),
            _ => null
        };
        if (file == null || !File.Exists(file))
            return BuildError(id, -32602, $"resource not found: {uri}");

        var mime = file.EndsWith(".json") ? "application/json" : "text/markdown";
        return BuildResult(id, new JObject
        {
            ["contents"] = new JArray { new JObject
            {
                ["uri"] = uri,
                ["mimeType"] = mime,
                ["text"] = File.ReadAllText(file)
            }}
        });
    }

    // ───────────── helpers ─────────────

    private static double ScoreNode(NodeSummary n, string ql)
    {
        double s = 0;
        if (n.Title.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 3;
        if (n.Tags.Any(t => t.Contains(ql, StringComparison.OrdinalIgnoreCase))) s += 2;
        if (n.Preview.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1;
        if (n.PrimaryCategory.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1.5;
        return s;
    }

    private static BrainExport? LoadExport()
    {
        var path = Path.Combine(_vaultPath, ".obsidianx", "brain-export.json");
        if (!File.Exists(path)) return null;
        try { return JsonConvert.DeserializeObject<BrainExport>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static readonly object _accessLogLock = new();

    /// <summary>
    /// Append an access event to access-log.ndjson. The 3D graph watcher
    /// tails this file and pulses the corresponding node on the graph.
    /// One line per event (NDJSON) so we can append without rewriting.
    /// </summary>
    private static void LogAccess(string nodeId, string op, string? context)
    {
        try
        {
            var dir = Path.Combine(_vaultPath, ".obsidianx");
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "access-log.ndjson");

            var entry = new JObject
            {
                ["ts"] = DateTime.UtcNow.ToString("O"),
                ["node_id"] = nodeId,
                ["op"] = op,
                ["client"] = "mcp",
                ["context"] = context ?? ""
            }.ToString(Formatting.None);

            lock (_accessLogLock)
            {
                // Keep the file bounded to avoid unbounded growth
                TrimIfLarge(logPath, maxBytes: 512 * 1024);
                File.AppendAllText(logPath, entry + "\n");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TrimIfLarge(string path, int maxBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < maxBytes) return;
            var lines = File.ReadAllLines(path);
            // keep last 2000 entries
            var kept = lines.Length > 2000 ? lines[^2000..] : lines;
            File.WriteAllLines(path, kept);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string ResolveVault(string[] args)
    {
        var env = Environment.GetEnvironmentVariable("OBSIDIANX_VAULT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        // Args: first arg that's not the .dll path itself
        foreach (var a in args.Skip(1))
            if (Directory.Exists(a)) return a;
        return @"G:\Obsidian";
    }

    private static void Log(string msg)
    {
        try { Console.Error.WriteLine($"[obsidianx-mcp] {msg}"); } catch { }
    }

    // ───────────── JSON-RPC framing ─────────────

    private static string BuildResult(JToken? id, JObject result)
    {
        var env = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["result"] = result
        };
        if (id != null) env["id"] = id;
        return env.ToString(Formatting.None);
    }

    private static string BuildError(JToken? id, int code, string message)
    {
        var env = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
        if (id != null) env["id"] = id;
        return env.ToString(Formatting.None);
    }
}
