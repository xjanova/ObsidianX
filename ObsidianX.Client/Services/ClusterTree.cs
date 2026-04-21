using System.Windows.Media.Media3D;
using ObsidianX.Core.Models;

namespace ObsidianX.Client.Services;

/// <summary>
/// A hierarchical tree of knowledge clusters — the brain graph seen as
/// a fractal. The root represents the whole brain; each internal node
/// is a cluster that contains sub-clusters (or leaf notes), built by
/// running label-propagation recursively on increasingly fine subgraphs.
///
/// Visual model: each cluster is a translucent "bubble" at overview
/// zoom. When the camera dives close enough, the bubble expands to
/// reveal its children — which may themselves be smaller bubbles.
/// Keep zooming and you eventually reach individual notes (leaves),
/// just like diving from a galaxy → solar system → planet → molecule.
/// </summary>
public class ClusterTree
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";
    public Point3D Center { get; set; }
    public double Radius { get; set; } = 1.0;
    public int LeafCount { get; set; }
    public KnowledgeCategory DominantCategory { get; set; }
    public List<ClusterTree> Children { get; set; } = [];
    public PhysicsNode? Leaf { get; set; }
    public int Depth { get; set; }
    public ClusterTree? Parent { get; set; }

    public bool IsLeaf => Leaf != null;

    /// <summary>
    /// Build a hierarchical cluster tree by recursively running label
    /// propagation on subgraphs. Stops at MinSize leaves or MaxDepth.
    /// </summary>
    public static ClusterTree Build(
        List<PhysicsNode> nodes,
        List<PhysicsEdge> edges,
        int minClusterSize = 4,
        int maxDepth = 5)
    {
        var root = new ClusterTree
        {
            Label = "Brain",
            Depth = 0,
        };

        if (nodes.Count == 0) return root;

        root.Children = BuildLevel(nodes, edges, depth: 1,
            maxDepth: maxDepth, minClusterSize: minClusterSize, parent: root);

        ComputeBounds(root);
        return root;
    }

    private static List<ClusterTree> BuildLevel(
        List<PhysicsNode> nodes,
        List<PhysicsEdge> edges,
        int depth,
        int maxDepth,
        int minClusterSize,
        ClusterTree parent)
    {
        var result = new List<ClusterTree>();

        // Leaf base case — 1 node, or reached depth/size floor
        if (nodes.Count <= 1 || depth >= maxDepth)
        {
            foreach (var n in nodes)
            {
                result.Add(new ClusterTree
                {
                    Leaf = n,
                    Label = n.Title,
                    Depth = depth,
                    Parent = parent,
                    LeafCount = 1,
                    DominantCategory = n.Category,
                    Center = n.Position,
                    Radius = n.Radius
                });
            }
            return result;
        }

        // Run label propagation on THIS subgraph only (edges between nodes in the set)
        var nodeSet = new HashSet<string>(nodes.Select(n => n.Id));
        var subEdges = edges
            .Where(e => nodeSet.Contains(e.SourceId) && nodeSet.Contains(e.TargetId))
            .ToList();

        var communities = PropagateLabels(nodes, subEdges, iterations: 6);

        foreach (var group in communities.GroupBy(kv => kv.Value))
        {
            var members = group.Select(kv => kv.Key).ToList();

            // Single-member "community" → leaf directly
            if (members.Count == 1)
            {
                var n = members[0];
                result.Add(new ClusterTree
                {
                    Leaf = n,
                    Label = n.Title,
                    Depth = depth,
                    Parent = parent,
                    LeafCount = 1,
                    DominantCategory = n.Category,
                    Center = n.Position,
                    Radius = n.Radius
                });
                continue;
            }

            // Tiny groups → flatten to leaves, don't make a sub-cluster
            if (members.Count < minClusterSize)
            {
                foreach (var n in members)
                {
                    result.Add(new ClusterTree
                    {
                        Leaf = n,
                        Label = n.Title,
                        Depth = depth,
                        Parent = parent,
                        LeafCount = 1,
                        DominantCategory = n.Category,
                        Center = n.Position,
                        Radius = n.Radius
                    });
                }
                continue;
            }

            // Real cluster — recurse into it
            var cluster = new ClusterTree
            {
                Depth = depth,
                Parent = parent,
                LeafCount = members.Count,
                DominantCategory = DominantCategoryOf(members),
                Label = LabelFor(members)
            };
            cluster.Children = BuildLevel(members, subEdges,
                depth + 1, maxDepth, minClusterSize, cluster);
            ComputeBounds(cluster);
            result.Add(cluster);
        }

        return result;
    }

    /// <summary>Label each node with a community id using label propagation.</summary>
    private static Dictionary<PhysicsNode, int> PropagateLabels(
        List<PhysicsNode> nodes, List<PhysicsEdge> edges, int iterations)
    {
        var labels = new Dictionary<PhysicsNode, int>();
        var idx = new Dictionary<string, int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            labels[nodes[i]] = i;
            idx[nodes[i].Id] = i;
        }

        var adj = new List<(PhysicsNode peer, double w)>[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) adj[i] = [];
        foreach (var e in edges)
        {
            if (!idx.TryGetValue(e.SourceId, out var si)) continue;
            if (!idx.TryGetValue(e.TargetId, out var ti)) continue;
            adj[si].Add((nodes[ti], e.Strength));
            adj[ti].Add((nodes[si], e.Strength));
        }

        var rng = new Random(nodes.Count);
        var order = Enumerable.Range(0, nodes.Count).ToList();

        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = order.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            bool changed = false;

            foreach (var i in order)
            {
                var neighbors = adj[i];
                if (neighbors.Count == 0) continue;

                var votes = new Dictionary<int, double>();
                foreach (var (peer, w) in neighbors)
                {
                    var lbl = labels[peer];
                    votes[lbl] = votes.GetValueOrDefault(lbl) + w;
                }

                int best = labels[nodes[i]];
                double bestW = votes.GetValueOrDefault(best, 0);
                foreach (var (lbl, w) in votes)
                {
                    if (w > bestW) { bestW = w; best = lbl; }
                }
                if (best != labels[nodes[i]]) { labels[nodes[i]] = best; changed = true; }
            }
            if (!changed) break;
        }

        return labels;
    }

    private static KnowledgeCategory DominantCategoryOf(List<PhysicsNode> nodes)
    {
        return nodes.GroupBy(n => n.Category)
                    .OrderByDescending(g => g.Count())
                    .First().Key;
    }

    private static string LabelFor(List<PhysicsNode> nodes)
    {
        var cat = DominantCategoryOf(nodes);
        var label = cat.ToString().Replace("_", " / ");
        return $"{label} ({nodes.Count})";
    }

    /// <summary>Recompute Center and Radius from current child positions.</summary>
    public static void ComputeBounds(ClusterTree t)
    {
        if (t.IsLeaf)
        {
            t.Center = t.Leaf!.Position;
            t.Radius = t.Leaf.Radius;
            return;
        }

        if (t.Children.Count == 0) return;

        // First compute children bounds (post-order)
        foreach (var c in t.Children) ComputeBounds(c);

        // Weighted center by leaf counts
        double cx = 0, cy = 0, cz = 0, total = 0;
        foreach (var c in t.Children)
        {
            var w = c.LeafCount;
            cx += c.Center.X * w;
            cy += c.Center.Y * w;
            cz += c.Center.Z * w;
            total += w;
        }
        if (total == 0) total = 1;
        t.Center = new Point3D(cx / total, cy / total, cz / total);

        // Radius = max distance from center to any child + that child's radius
        double r = 0;
        foreach (var c in t.Children)
        {
            var dx = c.Center.X - t.Center.X;
            var dy = c.Center.Y - t.Center.Y;
            var dz = c.Center.Z - t.Center.Z;
            var d = Math.Sqrt(dx * dx + dy * dy + dz * dz) + c.Radius;
            if (d > r) r = d;
        }
        t.Radius = Math.Max(r, 0.5);
        t.LeafCount = t.Children.Sum(c => c.LeafCount);
    }

    /// <summary>Walk the tree and decide at each cluster whether to expand or render as a bubble.</summary>
    public void Walk(
        Point3D focus,
        double camDist,
        Action<PhysicsNode> onLeaf,
        Action<ClusterTree> onBubble)
    {
        if (IsLeaf)
        {
            onLeaf(Leaf!);
            return;
        }

        // Distance from camera focus to this cluster's center
        var dx = Center.X - focus.X;
        var dy = Center.Y - focus.Y;
        var dz = Center.Z - focus.Z;
        var distToFocus = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        // Expansion rule:
        //   Expand if the camera is zoomed in enough AND the focus is
        //   close to this cluster. Otherwise, draw the bubble and stop.
        //
        //   Deeper clusters expand later (need closer zoom) because
        //   they're inside their parent.
        var depthFactor = Math.Pow(0.6, Depth);           // children need closer zoom
        var expandWhen = camDist < Radius * 6 * depthFactor
                      && distToFocus < Radius * 4;

        // Root (Depth 0) is always expanded
        if (Depth == 0) expandWhen = true;

        if (expandWhen)
        {
            foreach (var c in Children) c.Walk(focus, camDist, onLeaf, onBubble);
        }
        else
        {
            onBubble(this);
        }
    }

    /// <summary>Find the deepest cluster bubble that contains the given world-space point.</summary>
    public ClusterTree? FindBubbleAt(Point3D worldPos, Point3D focus, double camDist)
    {
        ClusterTree? hit = null;
        double bestDepth = -1;

        Walk(focus, camDist,
            onLeaf: _ => { },
            onBubble: bubble =>
            {
                var dx = worldPos.X - bubble.Center.X;
                var dy = worldPos.Y - bubble.Center.Y;
                var dz = worldPos.Z - bubble.Center.Z;
                var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < bubble.Radius && bubble.Depth > bestDepth)
                {
                    hit = bubble;
                    bestDepth = bubble.Depth;
                }
            });
        return hit;
    }
}
