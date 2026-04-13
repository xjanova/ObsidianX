using System.Text.RegularExpressions;
using ObsidianX.Core.Models;

namespace ObsidianX.Core.Services;

public partial class KnowledgeIndexer
{
    private static readonly Dictionary<KnowledgeCategory, string[]> CategoryKeywords = new()
    {
        [KnowledgeCategory.Programming] = ["code", "function", "class", "algorithm", "variable", "loop", "array", "api", "debug", "compiler", "syntax", "git", "repository", "refactor", "IDE"],
        [KnowledgeCategory.AI_MachineLearning] = ["neural", "network", "model", "training", "deep learning", "GPT", "transformer", "tensor", "classification", "regression", "NLP", "computer vision", "embedding", "LLM", "prompt"],
        [KnowledgeCategory.Blockchain_Web3] = ["blockchain", "smart contract", "token", "wallet", "defi", "NFT", "ethereum", "solidity", "web3", "decentralized", "consensus", "mining", "hash", "crypto"],
        [KnowledgeCategory.Science] = ["experiment", "hypothesis", "theory", "research", "physics", "chemistry", "biology", "quantum", "molecular", "atom", "energy", "force", "gravity"],
        [KnowledgeCategory.Mathematics] = ["equation", "theorem", "proof", "calculus", "algebra", "geometry", "statistics", "probability", "matrix", "integral", "derivative", "topology"],
        [KnowledgeCategory.Engineering] = ["system", "design", "architecture", "circuit", "mechanical", "electrical", "structural", "CAD", "simulation", "prototype", "manufacturing"],
        [KnowledgeCategory.Design_Art] = ["design", "color", "typography", "layout", "UI", "UX", "illustration", "graphic", "aesthetic", "composition", "palette", "figma", "sketch"],
        [KnowledgeCategory.Business_Finance] = ["market", "revenue", "strategy", "investment", "ROI", "startup", "equity", "valuation", "profit", "growth", "customer", "product"],
        [KnowledgeCategory.Security_Crypto] = ["security", "encryption", "vulnerability", "exploit", "firewall", "authentication", "authorization", "pentest", "malware", "CVE", "zero-day"],
        [KnowledgeCategory.DevOps_Cloud] = ["docker", "kubernetes", "CI/CD", "pipeline", "AWS", "Azure", "GCP", "terraform", "deployment", "container", "microservice", "serverless"],
        [KnowledgeCategory.Web_Development] = ["HTML", "CSS", "JavaScript", "React", "Vue", "Angular", "frontend", "backend", "REST", "GraphQL", "responsive", "SPA", "webpack"],
        [KnowledgeCategory.DataScience] = ["data", "analysis", "visualization", "pandas", "dataset", "ETL", "pipeline", "dashboard", "metric", "insight", "SQL", "warehouse"],
        [KnowledgeCategory.Health_Medicine] = ["health", "medical", "diagnosis", "treatment", "symptom", "disease", "therapy", "clinical", "patient", "pharmaceutical"],
        [KnowledgeCategory.Philosophy] = ["philosophy", "ethics", "consciousness", "existence", "logic", "metaphysics", "epistemology", "moral", "ontology"],
        [KnowledgeCategory.GameDev] = ["game", "unity", "unreal", "sprite", "shader", "physics engine", "gameplay", "level design", "multiplayer", "rendering"],
    };

    public KnowledgeGraph IndexVault(string vaultPath)
    {
        var graph = new KnowledgeGraph();
        if (!Directory.Exists(vaultPath)) return graph;

        var mdFiles = Directory.GetFiles(vaultPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".obsidian") && !f.Contains(".trash"));

        var nodeMap = new Dictionary<string, KnowledgeNode>();

        foreach (var file in mdFiles)
        {
            var node = IndexFile(file, vaultPath);
            graph.Nodes.Add(node);
            nodeMap[node.Title.ToLowerInvariant()] = node;
        }

        // Build edges from [[wiki-links]]
        foreach (var node in graph.Nodes)
        {
            var content = File.ReadAllText(node.FilePath);
            var links = WikiLinkPattern().Matches(content);
            foreach (Match link in links)
            {
                var target = link.Groups[1].Value.ToLowerInvariant();
                if (nodeMap.TryGetValue(target, out var targetNode) && targetNode.Id != node.Id)
                {
                    node.LinkedNodeIds.Add(targetNode.Id);
                    graph.Edges.Add(new KnowledgeEdge
                    {
                        SourceId = node.Id,
                        TargetId = targetNode.Id,
                        Strength = CalculateLinkStrength(node, targetNode),
                        RelationType = "wiki-link"
                    });
                }
            }
        }

        // Build expertise map
        foreach (var category in Enum.GetValues<KnowledgeCategory>())
        {
            var categoryNodes = graph.Nodes.Where(n =>
                n.PrimaryCategory == category || n.SecondaryCategories.Contains(category)).ToList();

            if (categoryNodes.Count == 0) continue;

            graph.ExpertiseMap[category] = new ExpertiseScore
            {
                Category = category,
                Score = Math.Min(1.0, categoryNodes.Sum(n => n.Importance) / 10.0),
                NoteCount = categoryNodes.Count,
                TotalWords = categoryNodes.Sum(n => n.WordCount),
                LastUpdated = categoryNodes.Max(n => n.ModifiedAt),
                GrowthRate = CalculateGrowthRate(categoryNodes)
            };
        }

        return graph;
    }

    private KnowledgeNode IndexFile(string filePath, string vaultPath)
    {
        var content = File.ReadAllText(filePath);
        var title = Path.GetFileNameWithoutExtension(filePath);
        var words = content.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var fileInfo = new FileInfo(filePath);

        // Extract tags from YAML frontmatter and #hashtags
        var tags = new List<string>();
        var yamlMatch = FrontmatterPattern().Match(content);
        if (yamlMatch.Success)
        {
            var tagMatches = YamlTagPattern().Matches(yamlMatch.Groups[1].Value);
            tags.AddRange(tagMatches.Select(m => m.Groups[1].Value));
        }
        tags.AddRange(HashtagPattern().Matches(content).Select(m => m.Groups[1].Value));

        // Categorize
        var scores = CalculateCategoryScores(content, tags);
        if (scores.Count == 0)
            scores[KnowledgeCategory.Other] = 0.1;
        var sorted = scores.OrderByDescending(kv => kv.Value).ToList();

        var node = new KnowledgeNode
        {
            Title = title,
            FilePath = filePath,
            PrimaryCategory = sorted[0].Key,
            SecondaryCategories = sorted.Skip(1).Take(3).Where(kv => kv.Value > 0.1).Select(kv => kv.Key).ToList(),
            Tags = tags.Distinct().ToList(),
            WordCount = words.Length,
            CreatedAt = fileInfo.CreationTimeUtc,
            ModifiedAt = fileInfo.LastWriteTimeUtc,
            Importance = Math.Log(1 + words.Length) * (1 + tags.Count * 0.1),
            KeywordScores = sorted.Take(5).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
        };

        return node;
    }

    private static Dictionary<KnowledgeCategory, double> CalculateCategoryScores(string content, List<string> tags)
    {
        var lower = content.ToLowerInvariant();
        var scores = new Dictionary<KnowledgeCategory, double>();

        foreach (var (category, keywords) in CategoryKeywords)
        {
            double score = 0;
            foreach (var keyword in keywords)
            {
                var count = CountOccurrences(lower, keyword.ToLowerInvariant());
                score += count * (1.0 / keywords.Length);
            }

            // Boost if tags match
            foreach (var tag in tags)
            {
                if (keywords.Any(k => tag.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    score += 2.0;
            }

            if (score > 0) scores[category] = Math.Min(1.0, score / 10.0);
        }

        if (scores.Count == 0)
            scores[KnowledgeCategory.Other] = 0.1;

        return scores;
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(pattern, i, StringComparison.Ordinal)) != -1) { count++; i += pattern.Length; }
        return count;
    }

    private static double CalculateLinkStrength(KnowledgeNode a, KnowledgeNode b)
    {
        double strength = 0.5;
        if (a.PrimaryCategory == b.PrimaryCategory) strength += 0.3;
        var sharedTags = a.Tags.Intersect(b.Tags).Count();
        strength += sharedTags * 0.1;
        return Math.Min(1.0, strength);
    }

    private static double CalculateGrowthRate(List<KnowledgeNode> nodes)
    {
        if (nodes.Count < 2) return 0;
        var recent = nodes.Count(n => n.ModifiedAt > DateTime.UtcNow.AddDays(-30));
        return (double)recent / nodes.Count;
    }

    [GeneratedRegex(@"\[\[([^\]|]+)(?:\|[^\]]+)?\]\]")]
    private static partial Regex WikiLinkPattern();

    [GeneratedRegex(@"---\s*\n(.*?)\n---", RegexOptions.Singleline)]
    private static partial Regex FrontmatterPattern();

    [GeneratedRegex(@"tags?:\s*\n(?:\s*-\s*(\w+)\n?)+|tags?:\s*\[([^\]]+)\]")]
    private static partial Regex YamlTagPattern();

    [GeneratedRegex(@"(?:^|\s)#(\w[\w/\-]+)", RegexOptions.Multiline)]
    private static partial Regex HashtagPattern();
}
