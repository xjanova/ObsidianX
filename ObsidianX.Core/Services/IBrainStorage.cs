using ObsidianX.Core.Models;

namespace ObsidianX.Core.Services;

/// <summary>
/// Storage abstraction for the brain's persistent data. Files on disk
/// remain the source of truth for note content; this layer caches the
/// parsed graph, serves fast indexed queries (FTS5 on SQLite, FULLTEXT
/// on MySQL), and persists access logs so we can answer "top queried
/// notes this week" without a full rescan.
///
/// Implementations:
///   FileBrainStorage   — JSON files only, current default behaviour
///   SqliteBrainStorage — single .obsidianx/brain.db, FTS5 search
///   MySqlBrainStorage  — shared server-hosted, FULLTEXT search
/// </summary>
public interface IBrainStorage : IDisposable
{
    string ProviderName { get; }

    /// <summary>Create or migrate schema. Safe to call repeatedly.</summary>
    void Initialize();

    /// <summary>Replace all nodes + edges with the current graph snapshot.</summary>
    void UpsertGraph(KnowledgeGraph graph);

    /// <summary>Full-text search. Returns ranked results.</summary>
    List<SearchResult> Search(string query, int limit = 25);

    /// <summary>Record that a node was accessed (MCP tool call, user click).</summary>
    void LogAccess(string nodeId, string op, string? context = null);

    /// <summary>Top N most-accessed nodes since the given time.</summary>
    List<AccessSummary> TopAccessed(int limit = 10, TimeSpan? window = null);

    /// <summary>Count nodes in the store (for UI telemetry).</summary>
    int NodeCount();
}

public class SearchResult
{
    public string NodeId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Snippet { get; set; } = string.Empty;
}

public class AccessSummary
{
    public string NodeId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Hits { get; set; }
    public DateTime LastAccessedAt { get; set; }
}
