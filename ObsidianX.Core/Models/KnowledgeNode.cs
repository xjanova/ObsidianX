namespace ObsidianX.Core.Models;

public class KnowledgeNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
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
