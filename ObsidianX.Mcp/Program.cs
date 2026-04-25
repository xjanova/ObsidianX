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
    private const string ServerVersion = "2.0.0";

    private static string _vaultPath = ResolveVault(Environment.GetCommandLineArgs());

    public static async Task<int> Main(string[] args)
    {
        // stdin/stdout must be UTF-8, no BOM; stderr is free for logs.
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        Log($"Starting MCP server · vault={_vaultPath}");

        // If ObsidianX client isn't running, bring it up. The MCP server
        // is spawned by Claude Desktop / Claude Code on first connection,
        // so this effectively "opens the brain visualiser automatically"
        // whenever the user starts talking to Claude.
        TryLaunchClientIfNotRunning();

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
            "This is xman's personal brain (ObsidianX), a living knowledge graph that grows over time.\n\n" +
            "AUTO-JOURNAL — The server AUTOMATICALLY logs every tool call you make to " +
            ".obsidianx/sessions/<date>.md. You never need to say 'I searched for X' — the brain is " +
            "already tracking it. Focus your writes on SUBSTANCE, not bookkeeping.\n\n" +
            "READING: brain_search (keyword), brain_get_note (full content by id), brain_expertise " +
            "(owner's domains ranked), brain_list (category/tag filter), brain_stats (overview). " +
            "ALWAYS prefer citing the owner's notes over generic answers — they represent actual " +
            "first-hand knowledge.\n\n" +
            "WRITING (proactively — this is how the brain gets smarter):\n" +
            "• brain_create_note — full standalone note with YAML frontmatter. Use when you solved a " +
            "  non-trivial problem, discovered a reusable technique, or the user says 'remember this' / " +
            "  'save' / 'add a note' / 'จำไว้' / 'บันทึก'. Pick a folder like Notes/Claude-Sessions, " +
            "  Programming, AI, Debugging, etc.\n" +
            "• brain_append_note — add content to an existing note (id from brain_search).\n" +
            "• brain_remember — ultra-lightweight one-liner to today's session journal. Use for small " +
            "  in-progress thoughts that don't warrant a standalone note. Perfect for 'oh interesting, " +
            "  so <library> behaves like <X>' mid-debugging.\n\n" +
            "Rule of thumb: if you just spent > 2 tool calls figuring something out and the answer " +
            "is non-trivial, SAVE IT. Every good answer should leave a trace in the vault."
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
                }),
            Tool("brain_create_note",
                "Create a new note in the brain. Writes a .md file under <vault>/<folder>/<title>.md " +
                "with YAML frontmatter and content. Use this when the user says 'remember that…', " +
                "'add a note about…', 'save this to my brain'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["title"] = new JObject { ["type"] = "string", ["description"] = "note title (will become file name)" },
                        ["content"] = new JObject { ["type"] = "string", ["description"] = "full markdown body" },
                        ["folder"] = new JObject { ["type"] = "string", ["description"] = "optional folder under vault, default 'Notes'" },
                        ["tags"] = new JObject { ["type"] = "string", ["description"] = "optional comma-separated tags added to frontmatter" }
                    },
                    ["required"] = new JArray { "title", "content" }
                }),
            Tool("brain_append_note",
                "Append content to an existing note. Identify by id (from brain_search) OR by path. " +
                "Use this when the user says 'add to <note>', 'append…', 'also remember…'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["id"] = new JObject { ["type"] = "string", ["description"] = "note id from brain_search/list" },
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "alternative: relative path under vault" },
                        ["content"] = new JObject { ["type"] = "string", ["description"] = "markdown to append (preceded by blank line)" }
                    },
                    ["required"] = new JArray { "content" }
                }),
            Tool("brain_remember",
                "Quick-save a short thought to today's session journal. Use when the insight " +
                "doesn't deserve its own note — e.g. small observations, one-liners, in-progress " +
                "ideas. Appended to .obsidianx/sessions/<date>.md under a '> REMEMBER:' quote.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "the thought to remember (markdown ok)" }
                    },
                    ["required"] = new JArray { "text" }
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
                "brain_search"       => BrainSearch(args),
                "brain_get_note"     => BrainGetNote(args),
                "brain_expertise"    => BrainExpertise(),
                "brain_list"         => BrainList(args),
                "brain_stats"        => BrainStats(),
                "brain_import_path"  => BrainImportPath(args),
                "brain_create_note"  => BrainCreateNote(args),
                "brain_append_note"  => BrainAppendNote(args),
                "brain_remember"     => BrainRemember(args),
                _ => throw new InvalidOperationException($"unknown tool: {name}")
            };

            // Auto-journal: every successful tool call leaves a trace in
            // the daily session log. The brain auto-records what happens.
            AutoLogSession(name ?? "unknown", SummarizeArgs(name, args));

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

    // ───────────── write tools ─────────────

    private static JToken BrainCreateNote(JObject args)
    {
        var title = args["title"]?.ToString() ?? throw new ArgumentException("title is required");
        var content = args["content"]?.ToString() ?? throw new ArgumentException("content is required");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is empty");

        var folder = (args["folder"]?.ToString() ?? "Notes").Trim();
        if (string.IsNullOrEmpty(folder)) folder = "Notes";

        var tagsStr = args["tags"]?.ToString() ?? "";
        var tags = tagsStr.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries
                                              | StringSplitOptions.TrimEntries);

        var safeTitle = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
        var safeFolder = string.Concat(folder.Split(Path.GetInvalidPathChars())).Trim();
        var relPath = Path.Combine(safeFolder, safeTitle + ".md");
        var fullPath = Path.Combine(_vaultPath, relPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        if (File.Exists(fullPath))
            throw new InvalidOperationException($"note already exists at {relPath} — use brain_append_note to add to it");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"created: {DateTime.UtcNow:O}");
        sb.AppendLine($"source: claude-mcp");
        if (tags.Length > 0)
        {
            sb.AppendLine("tags:");
            foreach (var t in tags) sb.AppendLine($"  - {t}");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.Append(content);
        File.WriteAllText(fullPath, sb.ToString());

        // Log the write so the client's Real Brain camera can fly here
        LogAccess(ComputeStableId(fullPath), "write", title);

        return new JObject
        {
            ["success"] = true,
            ["path"] = relPath.Replace("\\", "/"),
            ["fullPath"] = fullPath,
            ["id"] = ComputeStableId(fullPath),
            ["bytes"] = sb.Length,
            ["hint"] = "ObsidianX client will pick this up on next re-index. Tell user to click Re-index or it auto-refreshes on editor save."
        };
    }

    private static JToken BrainAppendNote(JObject args)
    {
        var content = args["content"]?.ToString() ?? throw new ArgumentException("content is required");
        var id = args["id"]?.ToString();
        var path = args["path"]?.ToString();

        string fullPath;
        string resolvedId;

        if (!string.IsNullOrEmpty(id))
        {
            var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
            var node = export.Nodes.FirstOrDefault(n => n.Id == id)
                ?? throw new InvalidOperationException($"note not found: {id}");
            fullPath = Path.Combine(export.VaultPath, node.RelativePath);
            resolvedId = id;
        }
        else if (!string.IsNullOrEmpty(path))
        {
            fullPath = Path.IsPathRooted(path) ? path : Path.Combine(_vaultPath, path);
            resolvedId = ComputeStableId(fullPath);
        }
        else throw new ArgumentException("id or path is required");

        if (!File.Exists(fullPath))
            throw new InvalidOperationException($"file not found: {fullPath}");

        // Append with a blank-line separator
        var existing = File.ReadAllText(fullPath);
        var separator = existing.EndsWith("\n\n") ? "" : existing.EndsWith("\n") ? "\n" : "\n\n";
        File.AppendAllText(fullPath, separator + content + "\n");

        LogAccess(resolvedId, "write", Path.GetFileNameWithoutExtension(fullPath));

        return new JObject
        {
            ["success"] = true,
            ["path"] = fullPath,
            ["id"] = resolvedId,
            ["appendedBytes"] = content.Length,
            ["hint"] = "Re-index in ObsidianX to update the graph."
        };
    }

    private static JToken BrainRemember(JObject args)
    {
        var text = args["text"]?.ToString() ?? throw new ArgumentException("text is required");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is empty");

        var now = DateTime.Now;
        var dir = Path.Combine(_vaultPath, ".obsidianx", "sessions");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{now:yyyy-MM-dd}.md");

        var block = new StringBuilder();
        block.AppendLine();
        block.AppendLine($"> **REMEMBER** `{now:HH:mm:ss}`  ");
        foreach (var line in text.Split('\n'))
            block.AppendLine($"> {line.TrimEnd()}");

        File.AppendAllText(path, block.ToString());

        return new JObject
        {
            ["success"] = true,
            ["path"] = Path.GetRelativePath(_vaultPath, path).Replace("\\", "/"),
            ["length"] = text.Length
        };
    }

    /// <summary>Compact one-line summary of a tool's args for the session journal.</summary>
    private static string? SummarizeArgs(string? tool, JObject args)
    {
        return tool switch
        {
            "brain_search"      => $"q=\"{args["query"]?.ToString()}\"",
            "brain_get_note"    => $"id={args["id"]?.ToString()}",
            "brain_list"        => $"category={args["category"]?.ToString() ?? "-"} tag={args["tag"]?.ToString() ?? "-"}",
            "brain_import_path" => $"path={args["path"]?.ToString()}",
            "brain_create_note" => $"title=\"{args["title"]?.ToString()}\" folder={args["folder"]?.ToString() ?? "Notes"}",
            "brain_append_note" => $"id={args["id"]?.ToString() ?? args["path"]?.ToString()}",
            "brain_remember"    => args["text"]?.ToString()?.Length is int n ? $"{n} chars" : null,
            _ => null
        };
    }

    /// <summary>Mirror of KnowledgeNode.IdFromPath so MCP-written notes
    /// carry the SAME id the client will compute on next re-index.</summary>
    private static string ComputeStableId(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return Guid.NewGuid().ToString("N")[..12];
        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
        var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
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
    private static readonly object _sessionLogLock = new();
    private static DateTime _lastSessionWrite = DateTime.MinValue;

    /// <summary>
    /// Auto-session journal — every tool call gets logged to
    /// <c>.obsidianx/sessions/YYYY-MM-DD.md</c> so the brain remembers
    /// what happened in every Claude session without Claude having to
    /// do anything. The user gets a permanent audit trail of what was
    /// asked, read, and written through MCP.
    /// A session header ("# Session ...") is written on the first call
    /// of the day OR after a 30-minute gap, so each focused sitting is
    /// its own section.
    /// </summary>
    private static void AutoLogSession(string tool, string? context, string? extra = null)
    {
        try
        {
            var now = DateTime.Now;   // local time for human readability
            var dir = Path.Combine(_vaultPath, ".obsidianx", "sessions");
            Directory.CreateDirectory(dir);
            var dailyPath = Path.Combine(dir, $"{now:yyyy-MM-dd}.md");

            lock (_sessionLogLock)
            {
                var sb = new StringBuilder();
                var isNewFile = !File.Exists(dailyPath);
                var gapFromLast = (DateTime.UtcNow - _lastSessionWrite).TotalMinutes;

                if (isNewFile)
                {
                    sb.AppendLine("---");
                    sb.AppendLine($"date: {now:yyyy-MM-dd}");
                    sb.AppendLine("source: claude-mcp-auto");
                    sb.AppendLine("tags:");
                    sb.AppendLine("  - session");
                    sb.AppendLine("  - auto-log");
                    sb.AppendLine("  - claude");
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.AppendLine($"# Brain Session — {now:yyyy-MM-dd}");
                    sb.AppendLine();
                }

                if (isNewFile || gapFromLast > 30)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## {now:HH:mm} — session opened");
                    sb.AppendLine();
                }

                var line = $"- `{now:HH:mm:ss}`  **{tool}**";
                if (!string.IsNullOrEmpty(context)) line += $"  ·  {EscapeMarkdown(context)}";
                if (!string.IsNullOrEmpty(extra))   line += $"  ·  {EscapeMarkdown(extra)}";
                sb.AppendLine(line);

                File.AppendAllText(dailyPath, sb.ToString());
                _lastSessionWrite = DateTime.UtcNow;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string EscapeMarkdown(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Cap length + escape pipe + strip newlines so one event = one line
        if (s.Length > 180) s = s[..180] + "…";
        return s.Replace("\n", " ").Replace("\r", "").Replace("|", "\\|");
    }

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

    /// <summary>
    /// Spawn ObsidianX.Client if no instance is already running. Walks
    /// up from the MCP exe's own location to find the Client build
    /// output, respecting both Release and Debug configurations. No-op
    /// if the client is already alive or the exe can't be found — MCP
    /// must never block on UI side-effects.
    /// </summary>
    private static void TryLaunchClientIfNotRunning()
    {
        try
        {
            // Already running? Leave it alone.
            if (System.Diagnostics.Process.GetProcessesByName("ObsidianX.Client").Length > 0)
                return;

            // The MCP exe lives at
            //   <solnRoot>/ObsidianX.Mcp/bin/<cfg>/net9.0/obsidianx-mcp.exe
            // Client sits at
            //   <solnRoot>/ObsidianX.Client/bin/<cfg>/net10.0-windows/ObsidianX.Client.exe
            var mcpExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(mcpExe)) mcpExe = Environment.GetCommandLineArgs()[0];
            var solnRoot = FindSolutionRoot(Path.GetDirectoryName(mcpExe) ?? "");
            if (solnRoot == null) { Log("client launch: solution root not found"); return; }

            // Candidate build outputs. We deliberately do NOT prefer Release
            // over Debug — when the developer has been iterating on Debug,
            // Release goes stale within hours and we'd auto-launch a
            // weeks-old binary. Pick the freshest by LastWriteTime instead.
            string[] candidates =
            [
                Path.Combine(solnRoot, "ObsidianX.Client", "bin", "Release", "net10.0-windows", "ObsidianX.Client.exe"),
                Path.Combine(solnRoot, "ObsidianX.Client", "bin", "Debug",   "net10.0-windows", "ObsidianX.Client.exe")
            ];

            var existing = candidates
                .Where(File.Exists)
                .Select(p => (path: p, mtime: File.GetLastWriteTimeUtc(p)))
                .OrderByDescending(t => t.mtime)
                .ToList();

            if (existing.Count == 0)
            {
                Log("client launch: exe not found under " + solnRoot);
                return;
            }

            var pick = existing[0].path;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pick,
                WorkingDirectory = Path.GetDirectoryName(pick)!,
                UseShellExecute = true,    // detach from our stdin/stdout
                CreateNoWindow = false
            };
            System.Diagnostics.Process.Start(psi);
            Log($"launched client (newest of {existing.Count}): {pick} @ {existing[0].mtime:O}");
        }
        catch (Exception ex) { Log($"client launch failed: {ex.Message}"); }
    }

    private static string? FindSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ObsidianX.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
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
