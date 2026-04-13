using System.Windows.Media.Media3D;
using ObsidianX.Core.Models;

namespace ObsidianX.Client.Services;

public class PhysicsNode
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public KnowledgeCategory Category { get; set; }
    public Point3D Position { get; set; }
    public Vector3D Velocity { get; set; }
    public double Mass { get; set; } = 1.0;
    public double Radius { get; set; } = 0.15;
    public bool IsDragging { get; set; }
    public bool IsHovered { get; set; }
    public double PulsePhase { get; set; }
    public int WordCount { get; set; }
    public List<string> LinkedIds { get; set; } = [];
}

public class PhysicsEdge
{
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double RestLength { get; set; } = 2.0;
    public double Strength { get; set; } = 1.0;
}

public class PhysicsEngine
{
    public List<PhysicsNode> Nodes { get; } = [];
    public List<PhysicsEdge> Edges { get; } = [];

    // Physics constants — tuned for bouncy feel
    public double Repulsion { get; set; } = 8.0;
    public double SpringStrength { get; set; } = 0.06;
    public double SpringLength { get; set; } = 2.5;
    public double Damping { get; set; } = 0.88;
    public double CenterGravity { get; set; } = 0.02;
    public double MaxVelocity { get; set; } = 2.0;
    public double BounceEnergy { get; set; } = 0.7;

    private readonly Random _rng = new();
    private double _totalEnergy;
    public double TotalEnergy => _totalEnergy;
    public bool IsSettled => _totalEnergy < 0.01;

    public void LoadFromGraph(KnowledgeGraph graph)
    {
        Nodes.Clear();
        Edges.Clear();

        // Place nodes on a sphere with some randomness
        int i = 0;
        int total = Math.Max(1, graph.Nodes.Count);
        foreach (var node in graph.Nodes)
        {
            // Fibonacci sphere distribution + jitter
            double phi = Math.Acos(1 - 2.0 * (i + 0.5) / total);
            double theta = Math.PI * (1 + Math.Sqrt(5)) * i;
            double r = 3.0 + (_rng.NextDouble() - 0.5) * 1.5;

            var pNode = new PhysicsNode
            {
                Id = node.Id,
                Title = node.Title,
                Category = node.PrimaryCategory,
                WordCount = node.WordCount,
                Position = new Point3D(
                    r * Math.Sin(phi) * Math.Cos(theta),
                    r * Math.Cos(phi),
                    r * Math.Sin(phi) * Math.Sin(theta)),
                Velocity = new Vector3D(
                    (_rng.NextDouble() - 0.5) * 0.5,
                    (_rng.NextDouble() - 0.5) * 0.5,
                    (_rng.NextDouble() - 0.5) * 0.5),
                Mass = Math.Max(0.5, Math.Log(1 + node.WordCount) * 0.3),
                Radius = Math.Max(0.08, Math.Min(0.35, Math.Log(1 + node.WordCount) * 0.035)),
                PulsePhase = _rng.NextDouble() * Math.PI * 2,
                LinkedIds = node.LinkedNodeIds
            };
            Nodes.Add(pNode);
            i++;
        }

        foreach (var edge in graph.Edges)
        {
            Edges.Add(new PhysicsEdge
            {
                SourceId = edge.SourceId,
                TargetId = edge.TargetId,
                RestLength = SpringLength,
                Strength = edge.Strength
            });
        }
    }

    /// <summary>
    /// Advance physics by one time step. Call this ~60fps.
    /// </summary>
    public void Step(double dt = 0.016)
    {
        if (Nodes.Count == 0) return;

        _totalEnergy = 0;
        var forces = new Vector3D[Nodes.Count];

        // === 1. Repulsion (Coulomb's law) — all pairs ===
        for (int i = 0; i < Nodes.Count; i++)
        {
            for (int j = i + 1; j < Nodes.Count; j++)
            {
                var delta = Nodes[i].Position - Nodes[j].Position;
                var dist = delta.Length;
                if (dist < 0.01) dist = 0.01;

                // Stronger repulsion when close
                var forceMag = Repulsion / (dist * dist);
                forceMag = Math.Min(forceMag, 50); // cap

                var dir = delta;
                dir.Normalize();
                var force = dir * forceMag;

                forces[i] += force;
                forces[j] -= force;
            }
        }

        // === 2. Spring forces (Hooke's law) — connected edges ===
        var nodeIndex = new Dictionary<string, int>();
        for (int i = 0; i < Nodes.Count; i++)
            nodeIndex[Nodes[i].Id] = i;

        foreach (var edge in Edges)
        {
            if (!nodeIndex.TryGetValue(edge.SourceId, out var si)) continue;
            if (!nodeIndex.TryGetValue(edge.TargetId, out var ti)) continue;

            var delta = Nodes[ti].Position - Nodes[si].Position;
            var dist = delta.Length;
            if (dist < 0.01) dist = 0.01;

            var displacement = dist - edge.RestLength;
            var forceMag = SpringStrength * displacement * edge.Strength;

            var dir = delta;
            dir.Normalize();
            var force = dir * forceMag;

            forces[si] += force;
            forces[ti] -= force;
        }

        // === 3. Center gravity — pull toward origin ===
        for (int i = 0; i < Nodes.Count; i++)
        {
            var toCenter = new Vector3D(0, 0, 0) - (Vector3D)Nodes[i].Position;
            forces[i] += toCenter * CenterGravity;
        }

        // === 4. Apply forces → velocity → position ===
        for (int i = 0; i < Nodes.Count; i++)
        {
            var node = Nodes[i];
            if (node.IsDragging) continue; // skip dragged nodes

            // F = ma → a = F/m
            var acceleration = forces[i] / node.Mass;

            // Integrate
            node.Velocity += acceleration * dt * 60; // normalize to 60fps
            node.Velocity *= Damping; // damping

            // Cap velocity
            if (node.Velocity.Length > MaxVelocity)
            {
                var v = node.Velocity;
                v.Normalize();
                node.Velocity = v * MaxVelocity;
            }

            node.Position += node.Velocity * dt * 60;

            // Soft boundary — bounce off sphere of radius 6
            var distFromCenter = ((Vector3D)node.Position).Length;
            if (distFromCenter > 6)
            {
                var normal = (Vector3D)node.Position;
                normal.Normalize();
                node.Position = (Point3D)(normal * 6);
                // Reflect velocity
                var dot = Vector3D.DotProduct(node.Velocity, normal);
                node.Velocity -= normal * (2 * dot);
                node.Velocity *= BounceEnergy;
            }

            _totalEnergy += node.Velocity.LengthSquared * node.Mass;
        }
    }

    /// <summary>
    /// Give a random kick to all nodes — re-animate the graph
    /// </summary>
    public void Disturb(double intensity = 1.0)
    {
        foreach (var node in Nodes)
        {
            node.Velocity += new Vector3D(
                (_rng.NextDouble() - 0.5) * intensity * 2,
                (_rng.NextDouble() - 0.5) * intensity * 2,
                (_rng.NextDouble() - 0.5) * intensity * 2);
        }
    }

    /// <summary>
    /// Kick a single node — triggered on click
    /// </summary>
    public void KickNode(int index, double intensity = 3.0)
    {
        if (index < 0 || index >= Nodes.Count) return;
        var node = Nodes[index];
        node.Velocity += new Vector3D(
            (_rng.NextDouble() - 0.5) * intensity,
            (_rng.NextDouble() - 0.5) * intensity,
            (_rng.NextDouble() - 0.5) * intensity);

        // Also kick neighbors gently
        foreach (var edge in Edges)
        {
            int? neighborIdx = null;
            if (edge.SourceId == node.Id && Nodes.FindIndex(n => n.Id == edge.TargetId) is >= 0 and var ti)
                neighborIdx = ti;
            else if (edge.TargetId == node.Id && Nodes.FindIndex(n => n.Id == edge.SourceId) is >= 0 and var si)
                neighborIdx = si;

            if (neighborIdx.HasValue)
            {
                Nodes[neighborIdx.Value].Velocity += new Vector3D(
                    (_rng.NextDouble() - 0.5) * intensity * 0.5,
                    (_rng.NextDouble() - 0.5) * intensity * 0.5,
                    (_rng.NextDouble() - 0.5) * intensity * 0.5);
            }
        }
    }

    /// <summary>
    /// Find nearest node to a 3D ray (for mouse picking)
    /// </summary>
    public int? HitTest(Point3D rayOrigin, Vector3D rayDir, double threshold = 0.4)
    {
        rayDir.Normalize();
        int? closest = null;
        double minDist = threshold;

        for (int i = 0; i < Nodes.Count; i++)
        {
            var toNode = Nodes[i].Position - rayOrigin;
            var proj = Vector3D.DotProduct(toNode, rayDir);
            if (proj < 0) continue;

            var closestPoint = rayOrigin + rayDir * proj;
            var dist = (Nodes[i].Position - closestPoint).Length;

            if (dist < minDist)
            {
                minDist = dist;
                closest = i;
            }
        }
        return closest;
    }
}
