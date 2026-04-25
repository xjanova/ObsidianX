using System.Text.RegularExpressions;
using ObsidianX.Core.Models;

namespace ObsidianX.Core.Services;

public partial class KnowledgeIndexer
{
    /// <summary>Optional auto-linker that adds semantic edges after indexing.</summary>
    public AutoLinker? AutoLinker { get; set; } = new();

    /// <summary>
    /// User-defined categories. When set, their keywords compete with
    /// the built-in ones; the best score wins and
    /// <see cref="KnowledgeNode.CustomCategoryId"/> is set accordingly.
    /// </summary>
    public CategoryRegistry? CustomCategories { get; set; }

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

        // Auto-link semantically related notes (runs after wiki-links so
        // user-authored edges take precedence)
        if (AutoLinker is { Options.Enabled: true })
            AutoLinker.AddAutoEdges(graph);

        // Build expertise map.
        //
        // Old formula was `Math.Min(1.0, sum / 10.0)` — `sum` of per-note
        // Importance (log-scaled words × tag boost) hits ~10 with just two
        // moderately-sized notes, so every populated category clamped to
        // 100% and the bars stopped saying anything. Even Mathematics
        // (2 notes) and Programming (292 notes) tied at full bar.
        //
        // New approach: rank relative to the user's strongest category.
        //   - Top category = 1.0 (their deepest area)
        //   - Others = their raw sum / top's raw sum, honestly scaled
        // Programming with 292 notes will dwarf Mathematics with 2 notes,
        // producing the long bar / thin bar contrast the UI is designed
        // to show.
        var byCategory = new Dictionary<KnowledgeCategory, List<KnowledgeNode>>();
        foreach (var category in Enum.GetValues<KnowledgeCategory>())
        {
            var nodes = graph.Nodes.Where(n =>
                n.PrimaryCategory == category || n.SecondaryCategories.Contains(category)).ToList();
            if (nodes.Count > 0) byCategory[category] = nodes;
        }

        double maxRaw = byCategory.Values
            .Select(ns => ns.Sum(n => n.Importance))
            .DefaultIfEmpty(0.0)
            .Max();

        foreach (var (category, nodes) in byCategory)
        {
            var raw = nodes.Sum(n => n.Importance);
            graph.ExpertiseMap[category] = new ExpertiseScore
            {
                Category = category,
                Score = maxRaw > 0 ? Math.Round(raw / maxRaw, 4) : 0,
                NoteCount = nodes.Count,
                TotalWords = nodes.Sum(n => n.WordCount),
                LastUpdated = nodes.Max(n => n.ModifiedAt),
                GrowthRate = CalculateGrowthRate(nodes)
            };
        }

        return graph;
    }

    private KnowledgeNode IndexFile(string filePath, string vaultPath)
    {
        var content = File.ReadAllText(filePath);
        var title = Path.GetFileNameWithoutExtension(filePath);
        var wordCount = ThaiTextSupport.CountWords(content);
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

        // Categorize — built-in categories
        var scores = CalculateCategoryScores(content, tags);
        if (scores.Count == 0)
            scores[KnowledgeCategory.Other] = 0.1;
        var sorted = scores.OrderByDescending(kv => kv.Value).ToList();

        // Custom categories compete head-to-head with built-ins
        string? customId = null;
        double customBestScore = 0;
        if (CustomCategories != null)
        {
            foreach (var cc in CustomCategories.All)
            {
                var s = ScoreCustomCategory(content, tags, cc);
                if (s > customBestScore)
                {
                    customBestScore = s;
                    customId = cc.Id;
                }
            }
        }

        // Built-in score of winner for comparison
        var builtInBest = sorted[0].Value;
        // Only assign custom if it clearly beat the built-in winner
        bool customWins = customBestScore > builtInBest * 1.15 && customBestScore > 0.15;

        var node = new KnowledgeNode
        {
            // Stable id from path — survives re-indexing so access-log
            // pulses, brain-export.json, and the live graph stay in sync.
            Id = KnowledgeNode.IdFromPath(filePath),
            Title = title,
            FilePath = filePath,
            PrimaryCategory = sorted[0].Key,
            SecondaryCategories = sorted.Skip(1).Take(3).Where(kv => kv.Value > 0.1).Select(kv => kv.Key).ToList(),
            Tags = tags.Distinct().ToList(),
            WordCount = wordCount,
            CreatedAt = fileInfo.CreationTimeUtc,
            ModifiedAt = fileInfo.LastWriteTimeUtc,
            Importance = Math.Log(1 + wordCount) * (1 + tags.Count * 0.1),
            KeywordScores = sorted.Take(5).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            CustomCategoryId = customWins ? customId : null
        };

        return node;
    }

    private static double ScoreCustomCategory(string content, List<string> tags, CustomCategory cc)
    {
        var lower = content.ToLowerInvariant();
        double score = 0;

        foreach (var kw in cc.KeywordsEn)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            var count = CountOccurrences(lower, kw.ToLowerInvariant());
            score += count * (1.0 / Math.Max(1, cc.KeywordsEn.Count));
        }
        foreach (var kw in cc.KeywordsTh)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            var count = CountOccurrences(content, kw);
            score += count * (1.0 / Math.Max(1, cc.KeywordsTh.Count));
        }

        foreach (var tag in tags)
        {
            if (cc.KeywordsEn.Any(k => tag.Contains(k, StringComparison.OrdinalIgnoreCase))) score += 2.0;
            if (cc.KeywordsTh.Any(k => tag.Contains(k, StringComparison.Ordinal))) score += 2.0;
            // Match tag against display name directly
            if (tag.Contains(cc.DisplayName, StringComparison.OrdinalIgnoreCase)) score += 2.5;
        }

        return Math.Min(1.0, score / 10.0);
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

            // Thai keywords (no lowercasing — Thai has no case)
            if (ThaiTextSupport.ThaiCategoryKeywords.TryGetValue(category, out var thaiKeywords))
            {
                foreach (var keyword in thaiKeywords)
                {
                    var count = CountOccurrences(content, keyword);
                    score += count * (1.0 / thaiKeywords.Length);
                }
            }

            // Boost if tags match (English or Thai)
            foreach (var tag in tags)
            {
                if (keywords.Any(k => tag.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    score += 2.0;
                if (thaiKeywords != null &&
                    thaiKeywords.Any(k => tag.Contains(k, StringComparison.Ordinal)))
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
