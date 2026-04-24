using System.Security.Cryptography;
using System.Text;

namespace ObsidianX.Core.Models;

public class KnowledgeNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Stable 12-char hex ID derived from the normalized file path.
    /// The brain graph relies on node IDs matching across re-index
    /// runs — otherwise brain-export.json on disk carries one set of
    /// IDs while the client's live graph uses different ones, so
    /// access-log pulses never land on the right node. Using a hash
    /// of the path gives us "same file → same id" forever.
    /// </summary>
    public static string IdFromPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return Guid.NewGuid().ToString("N")[..12];
        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public KnowledgeCategory PrimaryCategory { get; set; }
    public List<KnowledgeCategory> SecondaryCategories { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> LinkedNodeIds { get; set; } = [];
    public int WordCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public double Importance { get; set; }
    public Dictionary<string, double> KeywordScores { get; set; } = new();

    /// <summary>
    /// If set, this note scored higher on a user-defined custom category
    /// than any built-in one. The renderer uses the custom color and
    /// display name; the enum <see cref="PrimaryCategory"/> still gets
    /// set to the best-matching built-in for backward compatibility.
    /// </summary>
    public string? CustomCategoryId { get; set; }
}

public class KnowledgeGraph
{
    public List<KnowledgeNode> Nodes { get; set; } = [];
    public List<KnowledgeEdge> Edges { get; set; } = [];
    public Dictionary<KnowledgeCategory, ExpertiseScore> ExpertiseMap { get; set; } = new();
    public long TotalNodes => Nodes.Count;
    public long TotalEdges => Edges.Count;
    public long TotalWords => Nodes.Sum(n => n.WordCount);
}

public class KnowledgeEdge
{
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public double Strength { get; set; } = 1.0;
    public string RelationType { get; set; } = "link";
}
