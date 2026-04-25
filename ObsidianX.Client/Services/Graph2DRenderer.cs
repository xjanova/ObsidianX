using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ObsidianX.Core.Models;

namespace ObsidianX.Client.Services;

/// <summary>
/// Lightweight 2D fallback view for the knowledge graph. WPF's Viewport3D
/// struggles past a few hundred nodes — every frame rebuilds meshes,
/// re-allocates materials, and runs lighting math. DrawingContext is
/// retained-mode 2D and skips all of that, which is several times faster
/// for the common case.
///
/// Same `PhysicsEngine` instance powers both 2D and 3D views; only the
/// projection layer differs. Z is dropped (orthographic) — the spread on
/// the dashboard map is small enough that depth doesn't carry useful
/// information, and orthographic is the fastest path.
/// </summary>
public class Graph2DRenderer : FrameworkElement
{
    public PhysicsEngine? Physics { get; set; }
    public int? SelectedIndex { get; set; }

    /// <summary>World-space point the camera is centered on.</summary>
    public Point ViewCenter { get; set; } = new(0, 0);

    /// <summary>Pixels per world unit — bigger = zoomed in.</summary>
    public double Scale { get; set; } = 30;

    private Color _accent = Color.FromRgb(0, 240, 255);
    private Color _secondary = Color.FromRgb(139, 92, 246);

    /// <summary>
    /// Optional category → color mapping. When set, nodes are filled by
    /// their `KnowledgeCategory` so the user can scan the graph and see
    /// at a glance which areas of the brain own which regions. When null,
    /// every node uses the theme accent.
    /// </summary>
    public Func<KnowledgeCategory, Color>? CategoryColorFn { get; set; }

    // Frozen brushes/pens — created once per theme change, reused every
    // frame. Allocating brushes inside OnRender for hundreds of nodes is
    // the single biggest source of GC stalls in WPF 2D rendering.
    private Brush _nodeBrush = Brushes.Cyan;
    private Brush _selectedBrush = Brushes.Magenta;
    private Brush _hotBrush = Brushes.Yellow;
    private Pen _edgeWikiPen = new(Brushes.Cyan, 1);
    private Pen _edgeAutoPen = new(Brushes.Magenta, 0.7);
    private Pen _selectedRingPen = new(Brushes.Magenta, 1.6);

    // Cache of frozen category brushes (keyed by category). Built lazily
    // on first OnRender, invalidated when CategoryColorFn changes via
    // SetTheme(). 24 categories × 1 brush each is cheap.
    private readonly Dictionary<KnowledgeCategory, Brush> _categoryBrushCache = new();

    public Graph2DRenderer()
    {
        RebuildBrushes();
    }

    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            nameof(Background), typeof(Brush), typeof(Graph2DRenderer),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Background
    {
        get => GetValue(BackgroundProperty) as Brush;
        set => SetValue(BackgroundProperty, value);
    }

    public void SetTheme(Color accent, Color secondary)
    {
        _accent = accent;
        _secondary = secondary;
        RebuildBrushes();
        // Categorical brushes don't depend on theme, but they may have been
        // built before CategoryColorFn was wired — clear so they rebuild.
        _categoryBrushCache.Clear();
        InvalidateVisual();
    }

    private Brush BrushForCategory(KnowledgeCategory c)
    {
        if (_categoryBrushCache.TryGetValue(c, out var b)) return b;
        var color = CategoryColorFn?.Invoke(c) ?? _accent;
        var fresh = new SolidColorBrush(color);
        if (fresh.CanFreeze) fresh.Freeze();
        _categoryBrushCache[c] = fresh;
        return fresh;
    }

    private void RebuildBrushes()
    {
        _nodeBrush = Freeze(new SolidColorBrush(_accent));
        _selectedBrush = Freeze(new SolidColorBrush(_secondary));
        _hotBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 224, 64)));
        _edgeWikiPen = Freeze(new Pen(
            Freeze(new SolidColorBrush(Color.FromArgb(90, _accent.R, _accent.G, _accent.B))), 1.0));
        _edgeAutoPen = Freeze(new Pen(
            Freeze(new SolidColorBrush(Color.FromArgb(45, _secondary.R, _secondary.G, _secondary.B))), 0.6));
        _selectedRingPen = Freeze(new Pen(_selectedBrush, 1.8));
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        if (f.CanFreeze && !f.IsFrozen) f.Freeze();
        return f;
    }

    public Point WorldToScreen(Point3D p) => new(
        ActualWidth / 2 + (p.X - ViewCenter.X) * Scale,
        ActualHeight / 2 - (p.Y - ViewCenter.Y) * Scale);

    public Point ScreenToWorld(Point p) => new(
        (p.X - ActualWidth / 2) / Scale + ViewCenter.X,
        -(p.Y - ActualHeight / 2) / Scale + ViewCenter.Y);

    /// <summary>
    /// Frame all nodes in the viewport. Call after physics has placed
    /// nodes and the element has a non-zero ActualWidth/Height.
    /// </summary>
    public void FitToContent(double margin = 1.2)
    {
        if (Physics == null || Physics.Nodes.Count == 0) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var n in Physics.Nodes)
        {
            var p = n.Position;
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        var spanX = Math.Max(1.0, maxX - minX);
        var spanY = Math.Max(1.0, maxY - minY);
        ViewCenter = new Point((minX + maxX) / 2, (minY + maxY) / 2);
        var sx = ActualWidth / (spanX * margin);
        var sy = ActualHeight / (spanY * margin);
        Scale = Math.Min(sx, sy);
        if (Scale < 4) Scale = 4;
        if (Scale > 200) Scale = 200;
    }

    /// <summary>
    /// Hit-test a screen point against drawn nodes. Returns the index of
    /// the closest matching node within its hit radius, or null.
    /// </summary>
    public int? HitTest(Point screenPoint)
    {
        if (Physics == null) return null;
        var nodes = Physics.Nodes;
        int bestI = -1;
        double bestD2 = double.MaxValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            var sp = WorldToScreen(nodes[i].Position);
            var dx = sp.X - screenPoint.X;
            var dy = sp.Y - screenPoint.Y;
            var d2 = dx * dx + dy * dy;
            var r = NodeRadius(nodes[i]) + 4; // generous click area
            if (d2 < r * r && d2 < bestD2)
            {
                bestD2 = d2;
                bestI = i;
            }
        }
        return bestI < 0 ? null : bestI;
    }

    private static double NodeRadius(PhysicsNode n)
    {
        // Mirrors the 3D "size by Importance" feel — log of word count for
        // gentle scaling, plus a kick from access intensity.
        var baseR = 3.0 + Math.Min(8.0, Math.Log(1 + n.WordCount) * 0.7);
        return baseR + n.AccessIntensity * 4.0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Background paints first so off-screen culling areas stay dark.
        if (Background != null)
            dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

        if (Physics == null) return;
        var nodes = Physics.Nodes;
        if (nodes.Count == 0) return;

        // Cache id → index for O(1) edge resolution. Edges store endpoint
        // ids as strings (matches the on-disk graph), but rendering needs
        // the live PhysicsNode object — without the dict, every edge does
        // a linear search through the node list.
        var idx = new Dictionary<string, int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++) idx[nodes[i].Id] = i;

        // ── Edges ──────────────────────────────────────────────────────
        // Drawn first so nodes overlap them. Off-screen culling is cheap
        // and saves a lot when zoomed in on a corner of a big graph.
        foreach (var e in Physics.Edges)
        {
            if (!idx.TryGetValue(e.SourceId, out var si)) continue;
            if (!idx.TryGetValue(e.TargetId, out var ti)) continue;
            var s = WorldToScreen(nodes[si].Position);
            var t = WorldToScreen(nodes[ti].Position);
            if ((s.X < 0 && t.X < 0) || (s.X > w && t.X > w)) continue;
            if ((s.Y < 0 && t.Y < 0) || (s.Y > h && t.Y > h)) continue;
            var pen = e.IsAuto ? _edgeAutoPen : _edgeWikiPen;
            dc.DrawLine(pen, s, t);
        }

        // ── Nodes ──────────────────────────────────────────────────────
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var p = WorldToScreen(n.Position);
            if (p.X < -20 || p.X > w + 20 || p.Y < -20 || p.Y > h + 20) continue;

            var lifeScale = n.BirthProgress * n.DeathProgress;
            var radius = NodeRadius(n) * lifeScale;
            if (radius < 0.5) continue;

            // Pick fill: hot (active access) > selected > category color
            // (if mapping provided) > theme accent fallback. Categorical
            // coloring lets the user "see" the brain's regions at a glance.
            Brush fill = n.AccessIntensity > 0.4
                ? _hotBrush
                : (i == SelectedIndex
                    ? _selectedBrush
                    : (CategoryColorFn != null ? BrushForCategory(n.Category) : _nodeBrush));

            dc.DrawEllipse(fill, null, p, radius, radius);

            // Halo around the selected node so it pops without a hover-detail panel.
            if (i == SelectedIndex)
                dc.DrawEllipse(null, _selectedRingPen, p, radius + 3, radius + 3);
        }
    }
}
