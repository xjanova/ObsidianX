using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using ObsidianX.Client.Editor;
using ObsidianX.Client.Services;
using ObsidianX.Core.Models;
using ObsidianX.Core.Services;

namespace ObsidianX.Client;

public partial class MainWindow : Window
{
    private readonly string _vaultPath;
    private readonly string _identityPath;
    private BrainIdentity _identity = null!;
    private KnowledgeGraph _graph = new();
    private readonly KnowledgeIndexer _indexer = new();
    private ClaudeIntegration _claude = null!;

    // Editor
    private MarkdownEditor _mdEditor = null!;

    // Network
    private readonly NetworkClient _network = new();
    private string _serverUrl = "http://localhost:5142/brain-hub";
    private readonly List<ShareRequest> _incomingShares = [];
    private readonly List<string> _shareHistory = [];

    // Auto-import + brain export
    private readonly VaultImporter _importer = new();
    private readonly BrainExporter _exporter = new();
    private readonly List<string> _scanPaths = [];
    private bool _scanWholeMachine;
    private string _scanPatterns = "CLAUDE.md;README.md;*.md";
    private bool _autoScanOnStartup;
    private VaultImporter.ImportMode _importMode = VaultImporter.ImportMode.Reference;
    private List<ScanHit> _lastScanHits = [];

    // Access-log tail — MCP writes here when Claude pulls knowledge
    private long _accessLogOffset;
    private DispatcherTimer? _accessLogTimer;
    private int _recentAccessCount;   // for status bar counter

    // AutoLinker config
    private bool _autoLinkEnabled = true;
    private bool _showAutoEdges = true;
    private double _autoLinkThreshold = 0.35;

    // Storage provider (File / Sqlite / MySql)
    private string _storageProvider = "Sqlite";
    private string _mySqlConnString = "";
    private IBrainStorage? _storage;

    // Graph rendering performance controls
    private int _maxVisibleNodes = 400;          // 0 = unlimited
    private double _cullDistance = 12.0;         // camera-space; 0 = disabled
    private bool _useClusterColors = true;       // tint nodes by community

    // Custom category registry (user-defined knowledge domains)
    private CategoryRegistry? _categories;

    // Physics
    private readonly PhysicsEngine _dashPhysics = new();
    private readonly PhysicsEngine _graphPhysics = new();
    private double _time;
    private int _frameCount;
    private DateTime _lastFpsTime = DateTime.Now;

    // Camera control
    private bool _isDragging;
    private Point _lastMouse;
    private double _camYaw, _camPitch = 15;
    private double _camDist = 10;
    private double _graphYaw, _graphPitch = 15;
    private double _graphDist = 14;

    // Camera target (what we look at) — moves away from origin in follow/random modes
    private Point3D _graphTarget = new(0, 0, 0);

    // Camera modes
    private enum CameraMode { Free, FollowPulse, Orbit, Overview, RandomWalk, RealBrain }
    private CameraMode _cameraMode = CameraMode.Free;
    private DateTime _lastRandomTargetChange = DateTime.UtcNow;
    private int _randomTargetIdx = -1;
    private readonly Random _cameraRng = new();

    // Real-brain attention state: camera dwells ≥5s per active node,
    // queueing simultaneous events so the camera doesn't jitter between
    // many concurrent pulses. PickNextAttention chooses the next target
    // by max(AccessIntensity × recency) when the current dwell expires.
    private PhysicsNode? _attentionTarget;
    private DateTime _attentionStartedAt;
    private const double AttentionDwellSeconds = 5.0;

    // Real-brain "home" — the view before Claude started poking around.
    // Camera returns here when activity stops so the user keeps their
    // bearings. Captured lazily when the first attention fires.
    private (Point3D target, double dist)? _realBrainHome;

    // Node selection
    private int? _selectedNodeDash;
    private int? _selectedNodeGraph;

    // Pre-built sphere mesh (shared for performance)
    private static readonly MeshGeometry3D SharedSphere = BuildUnitSphere(10, 6);
    private static readonly MeshGeometry3D SharedSphereLOD = BuildUnitSphere(6, 4);
    private static readonly MeshGeometry3D SharedSphereTiny = BuildUnitSphere(4, 3);

    // Static starfield — generated once, rendered as tiny dots at large radius
    // so the graph looks like it's floating in deep space.
    private static readonly List<(Point3D pos, double r, Color c)> Starfield = BuildStarfield(350);

    private readonly Dictionary<string, string> _viewMap = new()
    {
        ["Dashboard"] = "DashboardView",
        ["BrainGraph"] = "BrainGraphView",
        ["Network"] = "NetworkView",
        ["Vault"] = "VaultView",
        ["Claude"] = "ClaudeView",
        ["Peers"] = "PeersView",
        ["Sharing"] = "SharingView",
        ["Growth"] = "GrowthView",
        ["Settings"] = "SettingsView",
        ["Editor"] = "EditorView",
        ["Search"] = "SearchView"
    };

    public MainWindow()
    {
        InitializeComponent();
        _vaultPath = @"G:\Obsidian";
        if (Environment.GetCommandLineArgs().Length > 1)
            _vaultPath = Environment.GetCommandLineArgs()[1];
        _identityPath = Path.Combine(_vaultPath, ".obsidianx", "identity.json");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var pulse = (Storyboard)FindResource("PulseAnimation");
        pulse.Begin();

        InitializeIdentity();
        LoadSettingsFromFile();
        IndexVault();
        CheckClaudeConnection();

        // Load physics
        _dashPhysics.LoadFromGraph(_graph);
        _dashPhysics.Disturb(_graph.TotalNodes > 20 ? 0.05 : 0.3);
        _graphPhysics.LoadFromGraph(_graph);
        _graphPhysics.Disturb(_graph.TotalNodes > 20 ? 0.08 : 0.4);

        // Populate UI
        UpdateUI();
        PopulateMatchCategories();
        PopulateSettings();
        PopulateImportSettings();
        PopulateMcpCommands();
        PopulateAutoLinkerSettings();
        PopulateStorageSettings();
        PopulateGraphPerfSettings();
        PopulateCustomCategories();
        StartAccessLogWatcher();
        StartMcpStatusWatcher();
        _ = LoadAiBackends();
        _ = RefreshAiKeyStatus();
        InitRedirectToggle();

        // Auto-scan + export on startup, if enabled
        if (_autoScanOnStartup && (_scanPaths.Count > 0 || _scanWholeMachine))
        {
            _ = Task.Run(() =>
            {
                var report = _importer.Scan(BuildImportOptions());
                if (report.Hits.Count > 0)
                {
                    _importer.Import(report.Hits, BuildImportOptions());
                    Dispatcher.Invoke(() =>
                    {
                        IndexVault();
                        _dashPhysics.LoadFromGraph(_graph);
                        _graphPhysics.LoadFromGraph(_graph);
                        UpdateUI();
                        RefreshVaultTree();
                        _exporter.Export(_vaultPath, _identity, _graph);
                        StatusText.Text = $"Auto-scan imported {report.Hits.Count} notes · brain exported";
                    });
                }
            });
        }
        else
        {
            // Still do an export so brain-export.json is fresh on launch
            try { _exporter.Export(_vaultPath, _identity, _graph); }
            catch (Exception ex) { Debug.WriteLine($"Auto-export failed: {ex.Message}"); }
        }

        // Wire network events (dispatch to UI thread)
        _network.StatusChanged += s => Dispatcher.Invoke(() => OnNetworkStatus(s));
        _network.PeerCountChanged += c => Dispatcher.Invoke(() => OnPeerCountChanged(c));
        _network.PeerJoined += p => Dispatcher.Invoke(() => OnPeerJoined(p));
        _network.PeerLeft += a => Dispatcher.Invoke(() => OnPeerLeft(a));
        _network.ShareRequested += r => Dispatcher.Invoke(() => OnShareRequested(r));
        _network.ShareResponseReceived += (f, a, t) => Dispatcher.Invoke(() => OnShareResponse(f, a, t));

        // Initialize markdown editor
        _mdEditor = new MarkdownEditor(MarkdownEditorControl, MarkdownPreview, _vaultPath);
        _mdEditor.WikiLinkClicked += OnWikiLinkClicked;
        _mdEditor.FileSaved += f =>
        {
            StatusText.Text = $"Saved: {Path.GetFileName(f)}";
            IndexVault();
            // Diff-based reload preserves positions for existing nodes,
            // animates new nodes as births and removed ones as deaths.
            _dashPhysics.LoadFromGraphDiff(_graph);
            _graphPhysics.LoadFromGraphDiff(_graph);
            UpdateUI();
            BumpNodeActivityByPath(f, "write");
        };
        _mdEditor.DirtyStateChanged += dirty => EditorDirtyIndicator.Text = dirty ? " *" : "";
        MarkdownEditorControl.TextArea.Caret.PositionChanged += (_, _) =>
        {
            EditorCursorPos.Text = $"Ln {MarkdownEditorControl.TextArea.Caret.Line}, Col {MarkdownEditorControl.TextArea.Caret.Column}";
            var words = MarkdownEditorControl.Text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
            EditorWordCount.Text = $"{words} words";
        };

        // Global keyboard shortcuts
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenQuickSwitcher), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(CreateNewNote), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusGraphSearch), Key.F, ModifierKeys.Control));

        // Start render loop
        CompositionTarget.Rendering += OnRenderFrame;
    }

    // ═══════════════════════════════════════
    // RENDER LOOP — called ~60fps by WPF
    // ═══════════════════════════════════════
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        _time += 0.016;
        _frameCount++;

        // FPS counter
        var now = DateTime.Now;
        if ((now - _lastFpsTime).TotalSeconds >= 1.0)
        {
            var nodesTotal = _graphPhysics.Nodes.Count;
            var communities = _graphPhysics.Nodes.Select(n => n.CommunityId).Distinct().Count();
            GraphFPS.Text = $"{_frameCount} FPS | {nodesTotal} nodes | {communities} clusters | E={_graphPhysics.TotalEnergy:F2}";
            _frameCount = 0;
            _lastFpsTime = now;
        }

        // Step physics
        _dashPhysics.Step();
        _graphPhysics.Step();

        // Decay knowledge-pulse intensity each frame (~16ms)
        DecayPulses(_dashPhysics, 0.016);
        DecayPulses(_graphPhysics, 0.016);

        // Rebuild 3D meshes (optimized: single batched geometry)
        if (DashboardView.Visibility == Visibility.Visible)
        {
            UpdateCamera(DashCam, _camYaw, _camPitch, _camDist);
            RebuildScene(BrainModel, _dashPhysics, _selectedNodeDash);
        }

        if (BrainGraphView.Visibility == Visibility.Visible)
        {
            UpdateGraphCameraAuto();
            UpdateCamera(GraphCam, _graphYaw, _graphPitch, _graphDist, _graphTarget);
            RebuildScene(FullGraphModel, _graphPhysics, _selectedNodeGraph);
            UpdateDepthBreadcrumb();
            UpdateScanline();
            UpdateActivityLabels();
        }
    }

    /// <summary>
    /// Float a small mono-font HUD tag next to every node that's currently
    /// being read or written (AccessIntensity > threshold). Labels project
    /// the node's 3D position to screen coords and fade with the pulse —
    /// "READ · CLAUDE.md" tags like a sci-fi tactical display.
    /// </summary>
    private readonly List<TextBlock> _activityLabelPool = [];
    private void UpdateActivityLabels()
    {
        if (ActivityLabels == null) return;
        var w = FullGraphViewport.ActualWidth;
        var h = FullGraphViewport.ActualHeight;
        if (w <= 0 || h <= 0) return;

        int used = 0;
        foreach (var n in _graphPhysics.Nodes)
        {
            if (n.AccessIntensity < 0.12) continue;

            if (!TryProjectToScreen(GraphCam, n.Position, w, h, out var sx, out var sy)) continue;

            var label = GetOrCreateActivityLabel(used++);
            var op = (DateTime.UtcNow - n.LastAccessedAt).TotalSeconds < 1.5 ? "▶ READ" : "◉ PULSE";
            label.Text = $"{op}  {TruncateTitle(n.Title, 28)}\n{n.Category}  ·  {n.WordCount:N0} w";
            label.Opacity = Math.Min(0.95, 0.3 + n.AccessIntensity * 0.7);
            Canvas.SetLeft(label, sx + 14);
            Canvas.SetTop(label, sy - 8);
            label.Visibility = Visibility.Visible;
        }

        // Hide unused labels
        for (int i = used; i < _activityLabelPool.Count; i++)
            _activityLabelPool[i].Visibility = Visibility.Collapsed;
    }

    private TextBlock GetOrCreateActivityLabel(int index)
    {
        while (_activityLabelPool.Count <= index)
        {
            var tb = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 10,
                Foreground = (SolidColorBrush)FindResource("NeonCyanBrush"),
                Padding = new Thickness(6, 2, 6, 2),
                Background = new SolidColorBrush(Color.FromArgb(140, 11, 11, 26)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)FindResource("NeonCyanColor"),
                    BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6
                }
            };
            ActivityLabels.Children.Add(tb);
            _activityLabelPool.Add(tb);
        }
        return _activityLabelPool[index];
    }

    private static string TruncateTitle(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        return s.Length > n ? s[..n] + "…" : s;
    }

    /// <summary>Project a world-space point to viewport screen coords.</summary>
    private static bool TryProjectToScreen(PerspectiveCamera cam, Point3D world,
        double viewportW, double viewportH, out double sx, out double sy)
    {
        sx = sy = -1;
        var forward = cam.LookDirection; forward.Normalize();
        var upIn = cam.UpDirection; upIn.Normalize();
        var right = Vector3D.CrossProduct(forward, upIn); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward); up.Normalize();

        var toWorld = world - cam.Position;
        var cz = Vector3D.DotProduct(toWorld, forward);
        if (cz <= 0.2) return false;   // behind or too close to camera plane

        var cx = Vector3D.DotProduct(toWorld, right);
        var cy = Vector3D.DotProduct(toWorld, up);

        var fovY = cam.FieldOfView * Math.PI / 180;
        var tanHalfY = Math.Tan(fovY / 2);
        var aspect = viewportW / viewportH;
        var tanHalfX = tanHalfY * aspect;

        var ndcX = cx / (cz * tanHalfX);
        var ndcY = cy / (cz * tanHalfY);
        if (Math.Abs(ndcX) > 1.4 || Math.Abs(ndcY) > 1.4) return false;

        sx = (ndcX + 1) * 0.5 * viewportW;
        sy = (1 - (ndcY + 1) * 0.5) * viewportH;
        return true;
    }

    /// <summary>
    /// Walks the scanline down the viewport on a slow loop. A full
    /// sweep takes ~5 seconds. Using the Height/Margin keeps it
    /// independent of actual viewport size.
    /// </summary>
    private void UpdateScanline()
    {
        if (Scanline == null || FullGraphViewport == null) return;
        var h = FullGraphViewport.ActualHeight;
        if (h <= 0) return;

        var phase = (_time * 0.2) % 1.0;        // one sweep per 5s
        var width = FullGraphViewport.ActualWidth;
        Scanline.Width = width;
        Scanline.Margin = new Thickness(0, phase * h, 0, 0);
        Scanline.Opacity = 0.15 + Math.Sin(phase * Math.PI) * 0.35;
    }

    /// <summary>
    /// Breadcrumb reflects what the camera is ACTUALLY rendering — the
    /// current scope — not just the deepest cluster that geometrically
    /// contains the target. FindCurrentScope checks both (focus inside
    /// radius) AND (camDist < Radius × 3), matching the renderer. So
    /// at overview zoom the breadcrumb reads "Brain" even if the target
    /// point happens to sit inside several nested spheres.
    /// </summary>
    private void UpdateDepthBreadcrumb()
    {
        var tree = _graphPhysics.ClusterTree;
        if (tree == null || DepthIndicator == null) return;

        int maxDepth = FindMaxDepth(tree);

        // Use the same scope logic the renderer uses — breadcrumb and
        // visuals stay in sync.
        var scope = FindCurrentScope(tree, _graphTarget, _graphDist) ?? tree;

        DepthIndicator.Text = $"Depth {scope.Depth} / {maxDepth}  ·  zoom {_graphDist:F1}";

        // Build breadcrumb from root → scope
        var path = new List<string>();
        var cur = scope;
        while (cur != null)
        {
            path.Insert(0, cur.Depth == 0 ? "Brain" : ShortLabel(cur.Label));
            cur = cur.Parent;
        }
        BreadcrumbText.Text = string.Join("  ›  ", path);
    }

    private static int FindMaxDepth(ClusterTree t)
    {
        int max = t.Depth;
        foreach (var c in t.Children)
        {
            var d = FindMaxDepth(c);
            if (d > max) max = d;
        }
        return max;
    }

    private static string ShortLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "cluster";
        return raw.Length > 30 ? raw[..30] + "…" : raw;
    }

    /// <summary>
    /// Auto-drive the graph camera based on the current CameraMode.
    /// Runs every frame; user-driven modes (Free) leave state untouched.
    /// </summary>
    private void UpdateGraphCameraAuto()
    {
        switch (_cameraMode)
        {
            case CameraMode.FollowPulse:
            {
                // Look at the node with the highest access intensity
                PhysicsNode? hot = null;
                double best = 0.15;
                foreach (var n in _graphPhysics.Nodes)
                {
                    if (n.AccessIntensity > best) { best = n.AccessIntensity; hot = n; }
                }
                if (hot != null) LerpGraphTarget(hot.Position, 0.05);
                // If nothing pulsing, gently drift back toward origin
                else LerpGraphTarget(new Point3D(0, 0, 0), 0.02);
                break;
            }
            case CameraMode.Orbit:
            {
                _graphYaw += 0.25;  // slow continuous rotation
                break;
            }
            case CameraMode.Overview:
            {
                // One-shot: fit the whole graph, then drop to Free so the
                // user's subsequent scroll doesn't get fought by per-frame
                // lerp pulling the camera back in.
                var b = ComputeBounds(_graphPhysics);
                _graphTarget = b.center;
                _graphDist = Math.Clamp(b.radius * 2.5, 6, 120);
                _cameraMode = CameraMode.Free;
                if (CameraModeCombo != null) CameraModeCombo.SelectedIndex = 0;
                break;
            }
            case CameraMode.RandomWalk:
            {
                if (_graphPhysics.Nodes.Count == 0) break;
                if ((DateTime.UtcNow - _lastRandomTargetChange).TotalSeconds > 8)
                {
                    _randomTargetIdx = _cameraRng.Next(_graphPhysics.Nodes.Count);
                    _lastRandomTargetChange = DateTime.UtcNow;
                }
                if (_randomTargetIdx >= 0 && _randomTargetIdx < _graphPhysics.Nodes.Count)
                {
                    LerpGraphTarget(_graphPhysics.Nodes[_randomTargetIdx].Position, 0.025);
                    _graphYaw += 0.05;  // gentle drift
                }
                break;
            }
            case CameraMode.RealBrain:
            {
                // Camera visits active nodes with a "home → dive → home"
                // pattern:
                //   • First activity → snapshot current view as home,
                //     lerp to active node, zoom in. get_note / write →
                //     deep zoom. search → moderate zoom.
                //   • New activity that scores higher than current →
                //     switch target immediately (no waiting for 5s dwell
                //     to expire). 5s minimum only applies to the same
                //     target — keeps the camera from bouncing off
                //     prematurely when a single note just got hit.
                //   • Activity dries up → lerp back to home so the user
                //     keeps spatial orientation.
                var now = DateTime.UtcNow;
                var newTarget = PickNextAttention();
                var dwellElapsed = _attentionTarget == null
                    ? double.MaxValue
                    : (now - _attentionStartedAt).TotalSeconds;

                if (_attentionTarget == null && newTarget != null)
                {
                    // First attention after idle — remember where we were
                    _realBrainHome ??= (_graphTarget, _graphDist);
                    _attentionTarget = newTarget;
                    _attentionStartedAt = now;
                }
                else if (newTarget != null && newTarget != _attentionTarget
                         && ScoreOfAttention(newTarget) > ScoreOfAttention(_attentionTarget) + 0.1)
                {
                    // A clearly hotter target appeared — switch now
                    _attentionTarget = newTarget;
                    _attentionStartedAt = now;
                }
                else if (_attentionTarget != null
                         && dwellElapsed >= AttentionDwellSeconds
                         && _attentionTarget.AccessIntensity < 0.12
                         && newTarget == null)
                {
                    // Current target faded, nothing else active — release
                    // attention; next frame will fly back home.
                    _attentionTarget = null;
                }

                if (_attentionTarget != null)
                {
                    LerpGraphTarget(_attentionTarget.Position, 0.08);

                    // Deep-pull (get_note/write within the last 3s) gets
                    // a tight close-up. search stays wider.
                    var deep = IsDeepOp(_attentionTarget.LastOp)
                            && (now - _attentionTarget.LastAccessedAt).TotalSeconds < 3.0;
                    var desired = deep
                        ? Math.Max(2.5, _attentionTarget.Radius * 8)
                        : Math.Max(6.0, _attentionTarget.Radius * 14);
                    _graphDist += (desired - _graphDist) * 0.05;

                    // Status feedback so the user can verify Real Brain is
                    // actually following something (and which thing).
                    if (StatusText != null)
                    {
                        var opLabel = deep ? "📖 READING" : "🔍 SCANNING";
                        StatusText.Text =
                            $"🧠 AI FOCUS · {opLabel} → {TruncateTitle(_attentionTarget.Title, 40)}";
                    }
                }
                else if (_realBrainHome.HasValue)
                {
                    // Return home — slower lerp so the trip feels intentional
                    LerpGraphTarget(_realBrainHome.Value.target, 0.04);
                    _graphDist += (_realBrainHome.Value.dist - _graphDist) * 0.03;
                    // Once we've arrived, forget the home so a future activity
                    // can snapshot a new one
                    var homeDistDelta = Math.Abs(_graphDist - _realBrainHome.Value.dist);
                    var homeTargetDelta = Distance(_graphTarget, _realBrainHome.Value.target);
                    if (homeDistDelta < 0.2 && homeTargetDelta < 0.2)
                        _realBrainHome = null;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Pick the next node deserving of camera attention. Scoring blends
    /// current access intensity (70%) with recency of the hit (30%) so
    /// a node that went active five seconds ago still ranks above one
    /// that was cold for a minute. Skips the current target to force a
    /// move when dwell expires.
    /// </summary>
    private PhysicsNode? PickNextAttention()
    {
        PhysicsNode? best = null;
        double bestScore = 0.12;   // minimum — below this nothing qualifies

        foreach (var n in _graphPhysics.Nodes)
        {
            if (n == _attentionTarget) continue;
            if (n.AccessIntensity < 0.05 && n.LastAccessedAt == DateTime.MinValue) continue;

            var score = ScoreOfAttention(n);
            if (score > bestScore) { bestScore = score; best = n; }
        }
        return best;
    }

    /// <summary>Compute attention score: intensity blended with recency.</summary>
    private static double ScoreOfAttention(PhysicsNode? n)
    {
        if (n == null) return 0;
        var recency = n.LastAccessedAt > DateTime.MinValue
            ? 1.0 / (1.0 + (DateTime.UtcNow - n.LastAccessedAt).TotalSeconds * 0.3)
            : 0;
        // Deep ops (read full note / write) get a recency bonus so they
        // win the switch contest against a mere search hit.
        var opBoost = IsDeepOp(n.LastOp) ? 0.2 : 0;
        return n.AccessIntensity * 0.7 + recency * 0.3 + opBoost;
    }

    private static bool IsDeepOp(string op) =>
        op.Equals("get_note", StringComparison.OrdinalIgnoreCase)
     || op.Equals("write", StringComparison.OrdinalIgnoreCase);

    private void LerpGraphTarget(Point3D to, double t)
    {
        _graphTarget = new Point3D(
            _graphTarget.X + (to.X - _graphTarget.X) * t,
            _graphTarget.Y + (to.Y - _graphTarget.Y) * t,
            _graphTarget.Z + (to.Z - _graphTarget.Z) * t);
    }

    /// <summary>Compute axis-aligned bounding sphere of the visible graph.</summary>
    private static (Point3D center, double radius) ComputeBounds(PhysicsEngine physics)
    {
        if (physics.Nodes.Count == 0) return (new Point3D(0, 0, 0), 8.0);

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var n in physics.Nodes)
        {
            if (n.Position.X < minX) minX = n.Position.X;
            if (n.Position.Y < minY) minY = n.Position.Y;
            if (n.Position.Z < minZ) minZ = n.Position.Z;
            if (n.Position.X > maxX) maxX = n.Position.X;
            if (n.Position.Y > maxY) maxY = n.Position.Y;
            if (n.Position.Z > maxZ) maxZ = n.Position.Z;
        }
        var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        var dx = maxX - center.X; var dy = maxY - center.Y; var dz = maxZ - center.Z;
        var r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (center, Math.Max(3, r));
    }

    // ═══════════════════════════════════════
    // OPTIMIZED 3D RENDERING — batched mesh
    // ═══════════════════════════════════════
    private void RebuildScene(ModelVisual3D parent, PhysicsEngine physics, int? selectedIdx)
    {
        var group = new Model3DGroup();

        // Sci-fi background layer (drawn first = behind everything)
        if (parent == FullGraphModel) BuildStarfieldScene(group);

        if (physics.Nodes.Count == 0)
        {
            BuildPlaceholderBrain(group);
            parent.Content = group;
            return;
        }

        // ── Fractal-zoom: walk the cluster tree and collect visible nodes
        //    plus bubbles that haven't been expanded yet ──
        bool[] visible = new bool[physics.Nodes.Count];
        var bubbles = new List<ClusterTree>();
        var nodeIndexById = new Dictionary<string, int>(physics.Nodes.Count);
        for (int i = 0; i < physics.Nodes.Count; i++) nodeIndexById[physics.Nodes[i].Id] = i;

        var tree = parent == FullGraphModel ? physics.ClusterTree : null;
        var fractalFocus = parent == BrainModel ? new Point3D(0, 0, 0) : _graphTarget;
        var fractalZoom = parent == BrainModel ? _camDist : _graphDist;

        if (tree != null)
        {
            tree.Walk(fractalFocus, fractalZoom,
                onLeaf: n => { if (nodeIndexById.TryGetValue(n.Id, out var idx)) visible[idx] = true; },
                onBubble: b => bubbles.Add(b));
        }
        else
        {
            // Dashboard view / fallback — no fractal, render every node
            // that has actual content. Empty/stale nodes stay invisible.
            for (int i = 0; i < visible.Length; i++)
            {
                var n = physics.Nodes[i];
                visible[i] = n.WordCount > 0 || !string.IsNullOrWhiteSpace(n.Title);
            }
        }

        // Apply MaxVisibleNodes cap only to the leaves that survived fractal walk
        if (_maxVisibleNodes > 0)
        {
            int visCount = visible.Count(v => v);
            if (visCount > _maxVisibleNodes)
            {
                var rankedKeep = physics.Nodes
                    .Select((n, i) => (i, score: n.Importance
                                              + (n.AccessIntensity > 0.05 ? 1e6 : 0)
                                              + (i == selectedIdx ? 1e7 : 0),
                                          visible: visible[i]))
                    .Where(t => t.visible)
                    .OrderByDescending(t => t.score)
                    .Take(_maxVisibleNodes)
                    .Select(t => t.i)
                    .ToHashSet();
                for (int i = 0; i < visible.Length; i++)
                    if (visible[i] && !rankedKeep.Contains(i)) visible[i] = false;
            }
        }

        // Camera + focus target — culling uses distance from what the user
        // is looking AT (LookAt point), not from the camera lens. This lets
        // the user "dive into" a cluster: near-focus nodes stay full-detail
        // while the rest of the graph falls away.
        var cam = parent == BrainModel ? DashCam : GraphCam;
        var camPos = cam.Position;
        var focus = parent == BrainModel
            ? new Point3D(0, 0, 0)   // dashboard always orbits origin
            : _graphTarget;
        var camDist = parent == BrainModel ? _camDist : _graphDist;

        // Focus radius expands with zoom-out so overview shows everything.
        // Zoom in → small radius → only nearby nodes visible.
        var focusRadius = _cullDistance > 0
            ? _cullDistance * Math.Max(1.0, camDist / 14.0)
            : double.MaxValue;
        var focusSqr = focusRadius * focusRadius;

        // Radius scale makes nodes smaller when zoomed in (for detail) and
        // keeps them visible-as-dots when zoomed way out. In the overview
        // (camDist > 50) we clamp up slightly so dots aren't invisible.
        double radiusScale = camDist < 6 ? 0.7 : camDist > 40 ? 1.25 : 1.0;

        // --- BATCH ALL NODES INTO ONE MESH PER MATERIAL ---
        var colorGroups = new Dictionary<Color, (MeshGeometry3D mesh, bool emissive)>();
        var glowGroup = new MeshGeometry3D();
        var pulseGroups = new Dictionary<Color, MeshGeometry3D>();
        var pulseAuraGroup = new MeshGeometry3D();
        int renderedCount = 0;

        for (int i = 0; i < physics.Nodes.Count; i++)
        {
            if (!visible[i]) continue;

            var node = physics.Nodes[i];

            // Focus-based culling: distance from LookAt, not camera
            var dx = node.Position.X - focus.X;
            var dy = node.Position.Y - focus.Y;
            var dz = node.Position.Z - focus.Z;
            var dsq = dx * dx + dy * dy + dz * dz;
            bool keepAnyway = i == selectedIdx || node.AccessIntensity > 0.05 || node.IsHovered;
            if (!keepAnyway && dsq > focusSqr) continue;

            renderedCount++;

            // Choose color: category color, tinted by community if enabled
            var baseColor = ResolveNodeColor(node);
            var color = _useClusterColors && node.CommunityId >= 0
                ? TintByCommunity(baseColor, node.CommunityId)
                : baseColor;

            var isSelected = i == selectedIdx;
            var pulse = 1.0 + Math.Sin(_time * 3 + node.PulsePhase) * 0.08;
            var radius = node.Radius * pulse * radiusScale;

            if (isSelected || node.IsHovered)
                radius *= 1.4;

            var intensity = node.AccessIntensity;
            if (intensity > 0.05)
            {
                var beat = 1.0 + Math.Sin(_time * 10 + node.PulsePhase) * 0.15 * intensity;
                radius *= (1.0 + 0.8 * intensity) * beat;
            }

            // ── Birth / death lifecycle animations ──
            //   Born: 0 → full size with a white-hot flash that fades in 0.8s.
            //   Dying: full → 0 over 1s, shrinking & fading.
            var birth = node.BirthProgress;
            var death = node.DeathProgress;
            if (birth < 1.0)
            {
                // Scale-in curve — starts very small with a burst
                radius *= 0.3 + 0.7 * birth;
            }
            if (death < 1.0)
            {
                radius *= death;
                if (death < 0.02) continue;   // fully dead, skip this frame
            }

            // LOD: far-from-focus nodes + large graphs use simple sphere
            var farLod = dsq > focusSqr * 0.3 || physics.Nodes.Count > 150 || camDist > 30;
            var sphereMesh = farLod && !isSelected ? SharedSphereLOD : SharedSphere;

            if (!colorGroups.ContainsKey(color))
                colorGroups[color] = (new MeshGeometry3D(), false);

            AppendSphereToMesh(colorGroups[color].mesh, node.Position, radius, sphereMesh);

            // Extra hot-layer: bright overlay mesh for pulsed nodes
            if (intensity > 0.05)
            {
                if (!pulseGroups.TryGetValue(color, out var pm))
                    pulseGroups[color] = pm = new MeshGeometry3D();
                AppendSphereToMesh(pm, node.Position, radius * 1.05, sphereMesh);
                // Outer aura scales with intensity
                AppendSphereToMesh(pulseAuraGroup, node.Position,
                    radius * (1.6 + intensity * 0.8), SharedSphereLOD);
            }

            // Birth flash — expanding white halo that fades as birth completes.
            // Dying version — similar but scaled by death progress so it trails off.
            if (birth < 1.0)
            {
                AppendSphereToMesh(pulseAuraGroup, node.Position,
                    radius * (2.0 + (1.0 - birth) * 2.5), SharedSphereLOD);
            }

            // Glow ring for selected
            if (isSelected)
                AppendSphereToMesh(glowGroup, node.Position, radius * 1.5, SharedSphereLOD);
        }

        // Add color-grouped node meshes
        foreach (var (color, (mesh, _)) in colorGroups)
        {
            var mat = new MaterialGroup();
            mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
            mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(100, color.R, color.G, color.B))));
            group.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }

        // Knowledge-pulse overlay: bright white-tinted emissive on top
        foreach (var (color, mesh) in pulseGroups)
        {
            // White-hot core tinted toward category color
            var hot = Color.FromArgb(230,
                (byte)Math.Min(255, color.R + 140),
                (byte)Math.Min(255, color.G + 140),
                (byte)Math.Min(255, color.B + 140));
            var pulseMat = new MaterialGroup();
            pulseMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(hot)));
            group.Children.Add(new GeometryModel3D(mesh, pulseMat));
        }

        // Outer translucent aura — cyan regardless of category so "MCP pull" reads
        // as a distinct signal on the graph
        if (pulseAuraGroup.Positions.Count > 0)
        {
            var auraMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(70, 0, 240, 255)));
            group.Children.Add(new GeometryModel3D(pulseAuraGroup, auraMat));
        }

        // Glow for selected
        if (glowGroup.Positions.Count > 0)
        {
            var glowMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(30, 0, 240, 255)));
            group.Children.Add(new GeometryModel3D(glowGroup, glowMat));
        }

        // --- BATCH EDGES — wiki (cyan, solid) vs auto (purple, faded) ---
        // Edge thickness scales with camera zoom so connections stay legible
        // in both overview and close-up. Only renders if at least one endpoint
        // is visible AND at least one is near the camera focus.
        var wikiEdgeMesh = new MeshGeometry3D();
        var autoEdgeMesh = new MeshGeometry3D();
        var idToIdx = new Dictionary<string, int>(physics.Nodes.Count);
        for (int i = 0; i < physics.Nodes.Count; i++) idToIdx[physics.Nodes[i].Id] = i;

        // Scale edge thickness with camera distance so it stays readable
        var edgeScale = Math.Clamp(camDist / 14.0, 0.5, 2.5);

        var newEdgeTipMesh = new MeshGeometry3D();   // bright pulse at the tip of growing edges

        foreach (var edge in physics.Edges)
        {
            if (edge.IsAuto && !_showAutoEdges) continue;
            if (!idToIdx.TryGetValue(edge.SourceId, out var srcIdx)) continue;
            if (!idToIdx.TryGetValue(edge.TargetId, out var tgtIdx)) continue;
            if (!visible[srcIdx] || !visible[tgtIdx]) continue;

            var src = physics.Nodes[srcIdx];
            var tgt = physics.Nodes[tgtIdx];
            var target = edge.IsAuto ? autoEdgeMesh : wikiEdgeMesh;
            var baseWidth = edge.IsAuto ? 0.006 : 0.012;

            // Edge formation animation — grow from source toward target
            var form = edge.FormProgress;
            if (form < 1.0)
            {
                // Draw the growing segment from src to (src + dir * form)
                var dx = tgt.Position.X - src.Position.X;
                var dy = tgt.Position.Y - src.Position.Y;
                var dz = tgt.Position.Z - src.Position.Z;
                var tip = new Point3D(
                    src.Position.X + dx * form,
                    src.Position.Y + dy * form,
                    src.Position.Z + dz * form);
                AppendLineToMesh(target, src.Position, tip, baseWidth * edgeScale * 1.4);
                // Bright ball at the tip — like a synapse firing
                AppendSphereToMesh(newEdgeTipMesh, tip, baseWidth * 3.0, SharedSphereLOD);
            }
            else
            {
                AppendLineToMesh(target, src.Position, tgt.Position, baseWidth * edgeScale);
            }
        }

        if (newEdgeTipMesh.Positions.Count > 0)
        {
            var tipMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(240, 255, 255, 255)));
            group.Children.Add(new GeometryModel3D(newEdgeTipMesh, tipMat));
        }

        if (wikiEdgeMesh.Positions.Count > 0)
        {
            var wikiMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(60, 0, 240, 255)));
            group.Children.Add(new GeometryModel3D(wikiEdgeMesh, wikiMat));
        }
        if (autoEdgeMesh.Positions.Count > 0)
        {
            // Electric purple, very translucent — auto-links are hints, not facts
            var autoMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(30, 139, 92, 246)));
            group.Children.Add(new GeometryModel3D(autoEdgeMesh, autoMat));
        }

        // Scope ring — holographic torus marking the boundary of the
        // cluster the camera is currently "inside". Gives the classic
        // sci-fi "you are here" feel.
        if (tree != null && parent == FullGraphModel)
        {
            var scope = FindCurrentScope(tree, fractalFocus, fractalZoom);
            if (scope != null && scope.Depth > 0)
                AppendScopeRing(group, scope.Center, scope.Radius,
                    GetCategoryColor(scope.DominantCategory));
        }

        // Pulse ripples — expanding rings on nodes hit by MCP access,
        // like a sonar ping. Independent of the node mesh pulse.
        AppendPulseRipples(group, physics, visible);

        // AI FOCUS indicator — a bright persistent halo + "AI FOCUS"
        // label around whatever Real Brain is currently attending to.
        // Easier to spot than the per-frame pulse because it's sticky
        // for the whole dwell period.
        if (_cameraMode == CameraMode.RealBrain
            && _attentionTarget != null
            && parent == FullGraphModel)
        {
            AppendAiFocusHalo(group, _attentionTarget);
        }

        // ── Fractal cluster bubbles ──
        // Each unexpanded cluster renders as a translucent sphere so users
        // see structure at overview zoom. Dive closer (shrink camDist OR
        // aim focus at the bubble) and it expands into its children next
        // frame. Supports click-to-dive via _pendingDiveBubble.
        if (bubbles.Count > 0)
        {
            var renderedBubbles = new List<(ClusterTree, double)>(bubbles.Count);

            var bubbleMeshes = new Dictionary<Color, MeshGeometry3D>();
            var bubbleOutlines = new Dictionary<Color, MeshGeometry3D>();

            foreach (var b in bubbles)
            {
                var baseColor = ResolveBubbleColor(b);
                var bubbleColor = _useClusterColors
                    ? TintByCommunity(baseColor, b.Id.GetHashCode())
                    : baseColor;

                if (!bubbleMeshes.TryGetValue(bubbleColor, out var bm))
                    bubbleMeshes[bubbleColor] = bm = new MeshGeometry3D();
                if (!bubbleOutlines.TryGetValue(bubbleColor, out var om))
                    bubbleOutlines[bubbleColor] = om = new MeshGeometry3D();

                // Inner filled sphere — sized for readability, not raw bounds
                // (bounds are often larger than visual needs and overlap peers)
                var visualR = Math.Min(b.Radius * 0.45, 0.6 + Math.Log(1 + b.LeafCount) * 0.18);
                AppendSphereToMesh(bm, b.Center, visualR, SharedSphereLOD);
                // Outer ring — slightly larger, hints at expansion
                AppendSphereToMesh(om, b.Center, visualR * 1.15, SharedSphereLOD);

                renderedBubbles.Add((b, visualR * 1.15));
            }

            _lastRenderedBubbles = renderedBubbles;

            foreach (var (color, mesh) in bubbleMeshes)
            {
                var fillMat = new MaterialGroup();
                fillMat.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                    Color.FromArgb(55, color.R, color.G, color.B))));
                fillMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                    Color.FromArgb(35, color.R, color.G, color.B))));
                group.Children.Add(new GeometryModel3D(mesh, fillMat) { BackMaterial = fillMat });
            }
            foreach (var (color, mesh) in bubbleOutlines)
            {
                var outlineMat = new EmissiveMaterial(new SolidColorBrush(
                    Color.FromArgb(90, color.R, color.G, color.B)));
                group.Children.Add(new GeometryModel3D(mesh, outlineMat));
            }
        }
        else
        {
            _lastRenderedBubbles = null;
        }

        parent.Content = group;
    }

    // Last rendered bubbles + their visual radii (so clicks map to what the
    // user actually sees, not the loose geometric bounding sphere).
    private List<(ClusterTree bubble, double visualR)>? _lastRenderedBubbles;

    // ═══════════════════════════════════════
    // SCI-FI VISUAL LAYERS
    // ═══════════════════════════════════════

    /// <summary>
    /// Emissive starfield on a large shell so the graph feels like
    /// it's floating in deep space. Drawn first so all 3D content
    /// sits in front of it. Alpha is low — stars don't compete with
    /// the actual graph for attention.
    /// </summary>
    private static void BuildStarfieldScene(Model3DGroup group)
    {
        // Batch by color for cheaper draw calls
        var batches = new Dictionary<Color, MeshGeometry3D>();
        foreach (var (pos, r, c) in Starfield)
        {
            if (!batches.TryGetValue(c, out var mesh))
                batches[c] = mesh = new MeshGeometry3D();
            AppendSphereToMesh(mesh, pos, r, SharedSphereTiny);
        }
        foreach (var (c, mesh) in batches)
        {
            var mat = new EmissiveMaterial(new SolidColorBrush(c));
            group.Children.Add(new GeometryModel3D(mesh, mat));
        }
    }

    /// <summary>
    /// Thin glowing torus at the equator of the current scope — the
    /// user's "you are inside this" indicator. Pulses with a slow
    /// sine so it reads as holographic rather than static.
    /// </summary>
    private void AppendScopeRing(Model3DGroup group, Point3D center, double radius, Color color)
    {
        var mesh = new MeshGeometry3D();
        int segments = 64;
        double thickness = radius * 0.012 * (1.0 + 0.3 * Math.Sin(_time * 2));
        double r = radius * 0.98;

        for (int i = 0; i < segments; i++)
        {
            double a0 = (double)i / segments * Math.PI * 2;
            double a1 = (double)(i + 1) / segments * Math.PI * 2;
            var p0 = new Point3D(center.X + r * Math.Cos(a0), center.Y, center.Z + r * Math.Sin(a0));
            var p1 = new Point3D(center.X + r * Math.Cos(a1), center.Y, center.Z + r * Math.Sin(a1));
            AppendLineToMesh(mesh, p0, p1, thickness);
        }

        var ringColor = Color.FromArgb(170,
            (byte)Math.Min(255, color.R + 80),
            (byte)Math.Min(255, color.G + 80),
            (byte)Math.Min(255, color.B + 80));
        var mat = new EmissiveMaterial(new SolidColorBrush(ringColor));
        group.Children.Add(new GeometryModel3D(mesh, mat));

        // Secondary perpendicular ring for holographic feel
        var mesh2 = new MeshGeometry3D();
        for (int i = 0; i < segments; i++)
        {
            double a0 = (double)i / segments * Math.PI * 2;
            double a1 = (double)(i + 1) / segments * Math.PI * 2;
            var p0 = new Point3D(center.X, center.Y + r * Math.Cos(a0), center.Z + r * Math.Sin(a0));
            var p1 = new Point3D(center.X, center.Y + r * Math.Cos(a1), center.Z + r * Math.Sin(a1));
            AppendLineToMesh(mesh2, p0, p1, thickness * 0.6);
        }
        var dimRing = Color.FromArgb(80, color.R, color.G, color.B);
        group.Children.Add(new GeometryModel3D(mesh2, new EmissiveMaterial(new SolidColorBrush(dimRing))));
    }

    /// <summary>
    /// Sonar-style expanding rings on nodes that got hit by MCP access
    /// in the last ~1.5 seconds. Independent layer so they glow even
    /// after the node-pulse decays.
    /// </summary>
    private void AppendPulseRipples(Model3DGroup group, PhysicsEngine physics, bool[] visible)
    {
        var mesh = new MeshGeometry3D();
        int count = 0;

        for (int i = 0; i < physics.Nodes.Count; i++)
        {
            if (!visible[i]) continue;
            var n = physics.Nodes[i];
            if (n.AccessIntensity < 0.1) continue;

            // The ripple radius expands with (1 - intensity) so a fresh
            // hit starts tiny and grows as it fades.
            var growth = 1.0 - n.AccessIntensity;
            var ringR = n.Radius * (2.0 + growth * 4.5);
            double thick = n.Radius * 0.08 * (0.4 + n.AccessIntensity * 0.8);

            int segs = 32;
            for (int s = 0; s < segs; s++)
            {
                double a0 = (double)s / segs * Math.PI * 2;
                double a1 = (double)(s + 1) / segs * Math.PI * 2;
                var p0 = new Point3D(n.Position.X + ringR * Math.Cos(a0), n.Position.Y, n.Position.Z + ringR * Math.Sin(a0));
                var p1 = new Point3D(n.Position.X + ringR * Math.Cos(a1), n.Position.Y, n.Position.Z + ringR * Math.Sin(a1));
                AppendLineToMesh(mesh, p0, p1, thick);
            }
            count++;
        }

        if (count > 0)
        {
            var rippleColor = Color.FromArgb(180, 0, 240, 255);
            group.Children.Add(new GeometryModel3D(mesh,
                new EmissiveMaterial(new SolidColorBrush(rippleColor))));
        }
    }

    /// <summary>
    /// Render a persistent pulsing halo ring + translucent aura around
    /// whatever node Real Brain mode is currently following, so the
    /// "AI is looking HERE" signal is unmistakable even between tool
    /// calls when the per-hit access pulse has decayed.
    /// </summary>
    private void AppendAiFocusHalo(Model3DGroup group, PhysicsNode target)
    {
        var breathe = 1.0 + Math.Sin(_time * 4) * 0.22;
        var baseR = target.Radius * 2.2 * breathe;

        // Twin rings — equator + meridian — like a tactical reticle
        var ringMesh = new MeshGeometry3D();
        int segs = 48;
        double thick = target.Radius * 0.08;
        for (int i = 0; i < segs; i++)
        {
            double a0 = (double)i / segs * Math.PI * 2;
            double a1 = (double)(i + 1) / segs * Math.PI * 2;
            var eq0 = new Point3D(target.Position.X + baseR * Math.Cos(a0),
                                  target.Position.Y,
                                  target.Position.Z + baseR * Math.Sin(a0));
            var eq1 = new Point3D(target.Position.X + baseR * Math.Cos(a1),
                                  target.Position.Y,
                                  target.Position.Z + baseR * Math.Sin(a1));
            AppendLineToMesh(ringMesh, eq0, eq1, thick);
            var me0 = new Point3D(target.Position.X + baseR * Math.Cos(a0),
                                  target.Position.Y + baseR * Math.Sin(a0),
                                  target.Position.Z);
            var me1 = new Point3D(target.Position.X + baseR * Math.Cos(a1),
                                  target.Position.Y + baseR * Math.Sin(a1),
                                  target.Position.Z);
            AppendLineToMesh(ringMesh, me0, me1, thick * 0.7);
        }
        group.Children.Add(new GeometryModel3D(ringMesh,
            new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(220, 0, 240, 255)))));

        // Outer translucent aura
        var haloMesh = new MeshGeometry3D();
        AppendSphereToMesh(haloMesh, target.Position, baseR * 1.15, SharedSphereLOD);
        group.Children.Add(new GeometryModel3D(haloMesh,
            new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(55, 0, 240, 255)))));
    }

    /// <summary>
    /// Which cluster is the camera currently "inside"? Requires BOTH
    /// the focus to be inside the cluster's sphere AND the camera to
    /// be close enough (distance &lt; radius × 3) that we're not just
    /// looking at the cluster from far away. Returns null when the
    /// camera is in overview mode for the whole graph.
    /// </summary>
    private static ClusterTree? FindCurrentScope(ClusterTree root, Point3D focus, double camDist)
    {
        // If camera is well outside the root's bounding sphere, we're
        // in pure overview — no scope.
        if (root.Depth == 0 && camDist > root.Radius * 1.8) return null;

        ClusterTree? deepest = null;
        void Visit(ClusterTree t)
        {
            if (t.IsLeaf) return;
            var dx = t.Center.X - focus.X;
            var dy = t.Center.Y - focus.Y;
            var dz = t.Center.Z - focus.Z;
            var distSq = dx * dx + dy * dy + dz * dz;
            // Inside the sphere AND close enough that we're committed to this cluster
            if (distSq < t.Radius * t.Radius && camDist < t.Radius * 2.5
                && (deepest == null || t.Depth > deepest.Depth))
                deepest = t;
            foreach (var c in t.Children) Visit(c);
        }
        Visit(root);
        return deepest;
    }

    /// <summary>Append a transformed sphere into a batched mesh (no new objects per node)</summary>
    private static void AppendSphereToMesh(MeshGeometry3D target, Point3D center, double radius, MeshGeometry3D unitSphere)
    {
        int baseIdx = target.Positions.Count;
        for (int i = 0; i < unitSphere.Positions.Count; i++)
        {
            var p = unitSphere.Positions[i];
            target.Positions.Add(new Point3D(
                p.X * radius + center.X,
                p.Y * radius + center.Y,
                p.Z * radius + center.Z));
            if (i < unitSphere.Normals.Count)
                target.Normals.Add(unitSphere.Normals[i]);
        }
        for (int i = 0; i < unitSphere.TriangleIndices.Count; i++)
            target.TriangleIndices.Add(unitSphere.TriangleIndices[i] + baseIdx);
    }

    private static void AppendLineToMesh(MeshGeometry3D mesh, Point3D from, Point3D to, double width)
    {
        var dir = to - from;
        if (dir.Length < 0.01) return;

        var perp = Vector3D.CrossProduct(dir, new Vector3D(0, 1, 0));
        if (perp.Length < 0.001) perp = Vector3D.CrossProduct(dir, new Vector3D(1, 0, 0));
        perp.Normalize();
        perp *= width;

        var perp2 = Vector3D.CrossProduct(dir, perp);
        perp2.Normalize();
        perp2 *= width;

        int b = mesh.Positions.Count;

        // Quad ribbon (2 triangles)
        mesh.Positions.Add(new Point3D(from.X + perp.X, from.Y + perp.Y, from.Z + perp.Z));
        mesh.Positions.Add(new Point3D(from.X - perp.X, from.Y - perp.Y, from.Z - perp.Z));
        mesh.Positions.Add(new Point3D(to.X + perp.X, to.Y + perp.Y, to.Z + perp.Z));
        mesh.Positions.Add(new Point3D(to.X - perp.X, to.Y - perp.Y, to.Z - perp.Z));

        mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 2); mesh.TriangleIndices.Add(b + 1);
        mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(b + 2); mesh.TriangleIndices.Add(b + 3);

        // Second ribbon perpendicular for visibility from any angle
        mesh.Positions.Add(new Point3D(from.X + perp2.X, from.Y + perp2.Y, from.Z + perp2.Z));
        mesh.Positions.Add(new Point3D(from.X - perp2.X, from.Y - perp2.Y, from.Z - perp2.Z));
        mesh.Positions.Add(new Point3D(to.X + perp2.X, to.Y + perp2.Y, to.Z + perp2.Z));
        mesh.Positions.Add(new Point3D(to.X - perp2.X, to.Y - perp2.Y, to.Z - perp2.Z));

        mesh.TriangleIndices.Add(b + 4); mesh.TriangleIndices.Add(b + 6); mesh.TriangleIndices.Add(b + 5);
        mesh.TriangleIndices.Add(b + 5); mesh.TriangleIndices.Add(b + 6); mesh.TriangleIndices.Add(b + 7);
    }

    private void BuildPlaceholderBrain(Model3DGroup group)
    {
        var rng = new Random(42);
        // Center brain
        var centerMesh = new MeshGeometry3D();
        AppendSphereToMesh(centerMesh, new Point3D(0, 0, 0), 1.5, SharedSphere);
        var centerMat = new MaterialGroup();
        centerMat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(60, 0, 240, 255))));
        centerMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(30, 139, 92, 246))));
        group.Children.Add(new GeometryModel3D(centerMesh, centerMat));

        var orbitMesh = new MeshGeometry3D();
        for (int i = 0; i < 16; i++)
        {
            double angle = i * Math.PI * 2 / 16;
            double r = 2.5 + rng.NextDouble() * 0.5;
            double y = (rng.NextDouble() - 0.5) * 2.5;
            var pos = new Point3D(Math.Cos(angle + _time * 0.3) * r, y, Math.Sin(angle + _time * 0.3) * r);
            AppendSphereToMesh(orbitMesh, pos, 0.08 + rng.NextDouble() * 0.12, SharedSphereLOD);
        }
        var orbitMat = new MaterialGroup();
        orbitMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(180, 0, 240, 255))));
        group.Children.Add(new GeometryModel3D(orbitMesh, orbitMat));
    }

    /// <summary>Random star points on a big shell for the background.</summary>
    private static List<(Point3D pos, double r, Color c)> BuildStarfield(int count)
    {
        var rng = new Random(42);
        var stars = new List<(Point3D pos, double r, Color c)>(count);
        for (int i = 0; i < count; i++)
        {
            // Fibonacci sphere on a large radius — stars fill all directions
            var t = (i + 0.5) / count;
            var phi = Math.Acos(1 - 2 * t);
            var theta = Math.PI * (1 + Math.Sqrt(5)) * i;
            var r = 60 + rng.NextDouble() * 40;
            var pos = new Point3D(
                r * Math.Sin(phi) * Math.Cos(theta),
                r * Math.Cos(phi),
                r * Math.Sin(phi) * Math.Sin(theta));

            // Mostly dim white, a few cyan/purple accent stars
            var pick = rng.NextDouble();
            Color col;
            byte alpha = (byte)(80 + rng.Next(120));
            if (pick < 0.07) col = Color.FromArgb(alpha, 0, 240, 255);         // cyan
            else if (pick < 0.12) col = Color.FromArgb(alpha, 139, 92, 246);   // purple
            else col = Color.FromArgb(alpha, 200, 200, 220);                   // white-ish

            var size = 0.05 + rng.NextDouble() * 0.15;
            stars.Add((pos, size, col));
        }
        return stars;
    }

    private static MeshGeometry3D BuildUnitSphere(int slices, int stacks)
    {
        var mesh = new MeshGeometry3D();
        for (int stack = 0; stack <= stacks; stack++)
        {
            double phi = Math.PI * stack / stacks;
            for (int slice = 0; slice <= slices; slice++)
            {
                double theta = 2 * Math.PI * slice / slices;
                double x = Math.Sin(phi) * Math.Cos(theta);
                double y = Math.Cos(phi);
                double z = Math.Sin(phi) * Math.Sin(theta);
                mesh.Positions.Add(new Point3D(x, y, z));
                mesh.Normals.Add(new Vector3D(x, y, z));
            }
        }
        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                int a = stack * (slices + 1) + slice;
                int b = a + slices + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1);
            }
        }
        return mesh;
    }

    // ═══════════════════════════════════════
    // CAMERA CONTROL
    // ═══════════════════════════════════════
    private static void UpdateCamera(PerspectiveCamera cam, double yaw, double pitch, double dist)
        => UpdateCamera(cam, yaw, pitch, dist, new Point3D(0, 0, 0));

    private static void UpdateCamera(PerspectiveCamera cam, double yaw, double pitch, double dist, Point3D target)
    {
        double yawRad = yaw * Math.PI / 180;
        double pitchRad = pitch * Math.PI / 180;
        var offset = new Vector3D(
            dist * Math.Sin(yawRad) * Math.Cos(pitchRad),
            dist * Math.Sin(pitchRad),
            dist * Math.Cos(yawRad) * Math.Cos(pitchRad));
        cam.Position = new Point3D(target.X + offset.X, target.Y + offset.Y, target.Z + offset.Z);
        cam.LookDirection = new Vector3D(-offset.X, -offset.Y, -offset.Z);
    }

    // ═══════════════════════════════════════
    // MOUSE HANDLERS — on Border wrapping Viewport3D
    // Border has Background="Transparent" so it catches ALL mouse events
    // ═══════════════════════════════════════

    // --- Dashboard ---
    private void Viewport_MouseDown(object s, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lastMouse = e.GetPosition((IInputElement)s);
        ((UIElement)s).CaptureMouse();

        // Hit test using the Viewport3D (child of this Border)
        var mouseOnViewport = e.GetPosition(BrainViewport);
        var hit = HitTestNode(BrainViewport, DashCam, _dashPhysics, mouseOnViewport);
        if (hit.HasValue)
        {
            _selectedNodeDash = hit;
            ShowNodeInfo(NodeInfoPanel, NodeInfoTitle, NodeInfoDetail, NodeInfoDot, NodeInfoContent, _dashPhysics.Nodes[hit.Value]);
        }
    }

    private void Viewport_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ((UIElement)s).ReleaseMouseCapture();
    }

    private void Viewport_MouseMove(object s, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition((IInputElement)s);
        var dx = pos.X - _lastMouse.X;
        var dy = pos.Y - _lastMouse.Y;
        _camYaw += dx * 0.5;
        _camPitch = Math.Clamp(_camPitch + dy * 0.3, -80, 80);
        _lastMouse = pos;
    }

    private void Viewport_MouseWheel(object s, MouseWheelEventArgs e)
    {
        _camDist = Math.Clamp(_camDist - e.Delta * 0.005, 3, 30);
    }

    private void Viewport_RightClick(object s, MouseButtonEventArgs e)
    {
        var mouseOnViewport = e.GetPosition(BrainViewport);
        var hit = HitTestNode(BrainViewport, DashCam, _dashPhysics, mouseOnViewport);
        if (hit.HasValue)
        {
            _dashPhysics.KickNode(hit.Value);
            _selectedNodeDash = hit;
            ShowNodeInfo(NodeInfoPanel, NodeInfoTitle, NodeInfoDetail, NodeInfoDot, NodeInfoContent, _dashPhysics.Nodes[hit.Value]);
        }
        else
        {
            _dashPhysics.Disturb(0.8);
        }
    }

    // --- Full Graph ---
    private void FullGraph_MouseDown(object s, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lastMouse = e.GetPosition((IInputElement)s);
        ((UIElement)s).CaptureMouse();

        var mouseOnViewport = e.GetPosition(FullGraphViewport);

        // Double-click anywhere: dive into the bubble under the cursor.
        // Single-click: select a leaf node (if hit) and show its info,
        // but leave bubbles alone so the user can orbit without diving.
        if (e.ClickCount == 2)
        {
            if (TryDiveIntoBubble(mouseOnViewport)) return;
            // Double-click on a leaf → dive to that leaf
            var leafHit = HitTestNode(FullGraphViewport, GraphCam, _graphPhysics, mouseOnViewport);
            if (leafHit.HasValue)
            {
                var n = _graphPhysics.Nodes[leafHit.Value];
                _graphTarget = n.Position;
                _graphDist = Math.Max(2.0, n.Radius * 8);
                _cameraMode = CameraMode.Free;
                if (CameraModeCombo != null) CameraModeCombo.SelectedIndex = 0;
                _selectedNodeGraph = leafHit;
                ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot, GraphNodeContent, n);
                StatusText.Text = $"🔍 Dived onto: {n.Title}";
            }
            return;
        }

        // Single-click priority: leaf-node hit first (specific data shown
        // in the info pane), bubble hit second (status-bar cluster info).
        var hit = HitTestNode(FullGraphViewport, GraphCam, _graphPhysics, mouseOnViewport);
        if (hit.HasValue)
        {
            _selectedNodeGraph = hit;
            ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot, GraphNodeContent, _graphPhysics.Nodes[hit.Value]);
        }
        else
        {
            // Fall back to bubble-hit so clicking a cluster isn't silent
            TrySelectBubble(mouseOnViewport);
        }
    }

    /// <summary>Cast a ray and find the bubble under the mouse, if any.</summary>
    private ClusterTree? HitTestBubble(Point mouseOnViewport)
    {
        if (_lastRenderedBubbles == null || _lastRenderedBubbles.Count == 0) return null;
        var ray = BuildPickRay(FullGraphViewport, GraphCam, mouseOnViewport);
        if (ray == null) return null;

        ClusterTree? best = null;
        double bestT = double.MaxValue;

        foreach (var (bubble, hitRadius) in _lastRenderedBubbles)
        {
            // Ray-sphere intersection against the VISIBLE radius (slightly
            // bigger than the rendered fill so clicks feel forgiving) —
            // not the loose geometric bounding sphere, which was making
            // hit zones overlap and swallow the smaller bubbles.
            var oc = ray.Value.origin - bubble.Center;
            var a = Vector3D.DotProduct(ray.Value.dir, ray.Value.dir);
            var bDot = 2 * Vector3D.DotProduct(ray.Value.dir, oc);
            var cDot = Vector3D.DotProduct(oc, oc) - hitRadius * hitRadius;
            var disc = bDot * bDot - 4 * a * cDot;
            if (disc < 0) continue;
            var t = (-bDot - Math.Sqrt(disc)) / (2 * a);
            if (t < 0) t = (-bDot + Math.Sqrt(disc)) / (2 * a);
            if (t > 0 && t < bestT) { bestT = t; best = bubble; }
        }
        return best;
    }

    /// <summary>Single-click on a bubble: show cluster info, don't dive.</summary>
    private bool TrySelectBubble(Point mouseOnViewport)
    {
        var hit = HitTestBubble(mouseOnViewport);
        if (hit == null) return false;

        // Summary: title, member count, top leaf titles inside
        var leafSamples = GatherLeafTitles(hit).Take(3).ToList();
        var sample = leafSamples.Count > 0 ? $" · notes: {string.Join(", ", leafSamples)}{(hit.LeafCount > 3 ? "…" : "")}" : "";
        StatusText.Text = $"🫧 Cluster: {hit.Label} · {hit.LeafCount} note(s){sample}  (double-click to dive)";
        return true;
    }

    /// <summary>Double-click on a bubble: dive into it.</summary>
    private bool TryDiveIntoBubble(Point mouseOnViewport)
    {
        var hit = HitTestBubble(mouseOnViewport);
        if (hit == null) return false;

        _graphTarget = hit.Center;
        _graphDist = Math.Max(2, hit.Radius * 2.4);
        SwitchToFreeCamera();
        StatusText.Text = $"🔍 Dived into cluster: {hit.Label} ({hit.LeafCount} notes)";
        return true;
    }

    private static IEnumerable<string> GatherLeafTitles(ClusterTree t)
    {
        if (t.IsLeaf) { yield return t.Leaf!.Title; yield break; }
        foreach (var c in t.Children)
            foreach (var title in GatherLeafTitles(c))
                yield return title;
    }

    private static (Point3D origin, Vector3D dir)? BuildPickRay(
        Viewport3D viewport, PerspectiveCamera cam, Point mouseOnViewport)
    {
        var w = viewport.ActualWidth;
        var h = viewport.ActualHeight;
        if (w <= 0 || h <= 0) return null;

        // Normalized device coords [-1, 1]
        var ndcX = (2.0 * mouseOnViewport.X / w) - 1.0;
        var ndcY = 1.0 - (2.0 * mouseOnViewport.Y / h);

        var forward = cam.LookDirection; forward.Normalize();
        var up = cam.UpDirection; up.Normalize();
        var right = Vector3D.CrossProduct(forward, up); right.Normalize();
        up = Vector3D.CrossProduct(right, forward); up.Normalize();

        var aspect = w / h;
        var fovYRad = cam.FieldOfView * Math.PI / 180;
        var tanHalfY = Math.Tan(fovYRad / 2);
        var tanHalfX = tanHalfY * aspect;

        var dir = forward + right * (ndcX * tanHalfX) + up * (ndcY * tanHalfY);
        dir.Normalize();
        return (cam.Position, dir);
    }

    private void FullGraph_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ((UIElement)s).ReleaseMouseCapture();
    }

    private void FullGraph_MouseMove(object s, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            // Not dragging — update hover tooltip instead
            UpdateHoverLabel(e.GetPosition((IInputElement)s));
            return;
        }
        var pos = e.GetPosition((IInputElement)s);
        var dx = pos.X - _lastMouse.X;
        var dy = pos.Y - _lastMouse.Y;

        // Drag ORBITS the camera around its target without cancelling
        // auto modes. If the user is in Real Brain, they can spin to
        // see the AI-focused node from different angles — the camera
        // keeps following. Only wheel zoom and clicks count as "I'll
        // drive now". This was the main reason Real Brain "wasn't
        // following" — a 1-pixel drag killed the mode.
        _graphYaw += dx * 0.5;
        _graphPitch = Math.Clamp(_graphPitch + dy * 0.3, -80, 80);
        _lastMouse = pos;
        if (HoverLabel != null) HoverLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Hover tooltip — shows the primary name of whatever the mouse is
    /// over. Leaf nodes show title + category. Cluster bubbles show the
    /// cluster label + member count + sample of leaf titles.
    /// </summary>
    private void UpdateHoverLabel(Point mouseOnElement)
    {
        if (HoverLabel == null) return;

        // Mouse position is relative to the catch-border; translate to viewport
        var mouseOnViewport = mouseOnElement;

        // Try leaf first — precise hit wins over a cluster covering the same spot
        var leafIdx = HitTestNode(FullGraphViewport, GraphCam, _graphPhysics, mouseOnViewport);
        if (leafIdx.HasValue)
        {
            var n = _graphPhysics.Nodes[leafIdx.Value];
            HoverTitle.Text = string.IsNullOrWhiteSpace(n.Title) ? "(untitled)" : n.Title;
            var customTag = "";
            if (!string.IsNullOrEmpty(n.CustomCategoryId) && _categories != null)
            {
                var cc = _categories.FindById(n.CustomCategoryId);
                if (cc != null) customTag = $"  ·  {cc.DisplayName}";
            }
            HoverSubtitle.Text = $"{n.Category}{customTag}  ·  {n.WordCount:N0} words";
            PlaceHoverLabel(mouseOnElement);
            return;
        }

        // Then bubble
        var bubbleHit = HitTestBubble(mouseOnViewport);
        if (bubbleHit != null)
        {
            HoverTitle.Text = bubbleHit.Label;
            var leafSamples = GatherLeafTitles(bubbleHit).Take(4).ToList();
            var sample = leafSamples.Count > 0 ? string.Join(" · ", leafSamples) : "no notes";
            var more = bubbleHit.LeafCount > leafSamples.Count ? $" +{bubbleHit.LeafCount - leafSamples.Count} more" : "";
            HoverSubtitle.Text = $"{bubbleHit.LeafCount} notes  ·  depth {bubbleHit.Depth}\n{sample}{more}";
            PlaceHoverLabel(mouseOnElement);
            return;
        }

        // Nothing under cursor
        HoverLabel.Visibility = Visibility.Collapsed;
    }

    private void PlaceHoverLabel(Point mouse)
    {
        var offsetX = mouse.X + 18;
        var offsetY = mouse.Y + 16;

        // Clamp into the viewport so tooltip doesn't get clipped off the edge
        var w = FullGraphViewport.ActualWidth;
        var h = FullGraphViewport.ActualHeight;
        if (offsetX + 300 > w) offsetX = mouse.X - 310;
        if (offsetY + 80 > h) offsetY = mouse.Y - 90;

        HoverLabel.Margin = new Thickness(offsetX, offsetY, 0, 0);
        HoverLabel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Drop the auto camera mode and sync the ComboBox. Called from any
    /// manual camera interaction (wheel, drag, click-to-dive, search
    /// fly-to) so the user's input isn't fought by a per-frame lerp.
    /// </summary>
    private void SwitchToFreeCamera()
    {
        if (_cameraMode == CameraMode.Free) return;
        _cameraMode = CameraMode.Free;
        if (CameraModeCombo != null) CameraModeCombo.SelectedIndex = 0;
    }

    private void FullGraph_MouseWheel(object s, MouseWheelEventArgs e)
    {
        // Wide zoom range: 2 (close-up inside a cluster) → 120 (full overview as dots)
        // User scrolled → take manual control (auto modes keep lerping
        // the camera back otherwise, which feels like the zoom "bounces").
        SwitchToFreeCamera();
        _graphDist = Math.Clamp(_graphDist - e.Delta * 0.012, 2, 120);
    }

    private void FullGraph_RightClick(object s, MouseButtonEventArgs e)
    {
        var mouseOnViewport = e.GetPosition(FullGraphViewport);
        var hit = HitTestNode(FullGraphViewport, GraphCam, _graphPhysics, mouseOnViewport);
        if (hit.HasValue)
        {
            _graphPhysics.KickNode(hit.Value);
            _selectedNodeGraph = hit;
            ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot, GraphNodeContent, _graphPhysics.Nodes[hit.Value]);
        }
        else
        {
            _graphPhysics.Disturb(1.0);
        }
    }

    // ═══════════════════════════════════════
    // HIT TESTING — find which node was clicked
    // ═══════════════════════════════════════
    private int? HitTestNode(Viewport3D viewport, PerspectiveCamera cam, PhysicsEngine physics, Point mousePos)
    {
        var rayOrigin = cam.Position;
        var vpSize = new Size(viewport.ActualWidth, viewport.ActualHeight);
        if (vpSize.Width < 1 || vpSize.Height < 1) return null;

        double fovRad = cam.FieldOfView * Math.PI / 180;
        double aspect = vpSize.Width / vpSize.Height;
        double ndcX = (2.0 * mousePos.X / vpSize.Width - 1.0) * aspect;
        double ndcY = 1.0 - 2.0 * mousePos.Y / vpSize.Height;

        var lookDir = cam.LookDirection;
        lookDir.Normalize();
        var right = Vector3D.CrossProduct(lookDir, cam.UpDirection);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, lookDir);
        up.Normalize();

        double tanFov = Math.Tan(fovRad / 2);
        var rayDir = lookDir + right * (ndcX * tanFov) + up * (ndcY * tanFov);
        rayDir.Normalize();

        // Tight hit — per-node radius (× 1.3 for forgiveness), not a fixed
        // threshold. Stops the hover tooltip from popping up when the cursor
        // is floating over empty space that just happens to be near a node.
        return physics.HitTestPerRadius(rayOrigin, rayDir, slack: 1.3);
    }

    private void ShowNodeInfo(Border panel, TextBlock titleBlock, TextBlock detailBlock,
        System.Windows.Shapes.Ellipse dot, TextBlock contentBlock, PhysicsNode node)
    {
        panel.Visibility = Visibility.Visible;
        titleBlock.Text = node.Title;
        detailBlock.Text = $"{node.Category.ToString().Replace("_", " / ")} · {node.WordCount:N0} words · {node.LinkedIds.Count} links";
        dot.Fill = new SolidColorBrush(GetCategoryColor(node.Category));

        // Load file content preview
        var graphNode = _graph.Nodes.FirstOrDefault(n => n.Id == node.Id);
        if (graphNode != null && File.Exists(graphNode.FilePath))
        {
            try
            {
                var content = File.ReadAllText(graphNode.FilePath);
                // Strip YAML frontmatter
                if (content.StartsWith("---"))
                {
                    var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
                    if (endIdx > 0) content = content[(endIdx + 3)..].TrimStart();
                }
                // Show first ~500 chars
                contentBlock.Text = content.Length > 500 ? content[..500] + "..." : content;
            }
            catch
            {
                contentBlock.Text = "(Could not read file)";
            }
        }
        else
        {
            contentBlock.Text = "(File not found)";
        }
    }

    // ═══════════════════════════════════════
    // BUTTON ACTIONS
    // ═══════════════════════════════════════
    private void KickNode_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNodeDash.HasValue)
            _dashPhysics.KickNode(_selectedNodeDash.Value);
    }

    private void KickGraphNode_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNodeGraph.HasValue)
            _graphPhysics.KickNode(_selectedNodeGraph.Value);
    }

    private void OpenDashNodeFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_selectedNodeDash.HasValue) return;
        var node = _dashPhysics.Nodes[_selectedNodeDash.Value];
        OpenNodeInObsidian(node.Id);
    }

    private void OpenNodeFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_selectedNodeGraph.HasValue) return;
        var node = _graphPhysics.Nodes[_selectedNodeGraph.Value];
        OpenNodeInObsidian(node.Id);
    }

    private void OpenNodeInObsidian(string nodeId)
    {
        var file = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId)?.FilePath;
        if (file != null && File.Exists(file))
        {
            try
            {
                var uri = $"obsidian://open?vault={Uri.EscapeDataString(Path.GetFileName(_vaultPath))}&file={Uri.EscapeDataString(Path.GetRelativePath(_vaultPath, file))}";
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Open in Obsidian failed: {ex.Message}");
                StatusText.Text = "Could not open file in Obsidian";
            }
        }
    }

    private void ShakeGraph_Click(object sender, RoutedEventArgs e) => _graphPhysics.Disturb(2.0);
    private void ResetCamera_Click(object sender, RoutedEventArgs e)
    {
        _graphYaw = 0; _graphPitch = 15; _graphDist = 14;
        _graphTarget = new Point3D(0, 0, 0);
    }

    private void CameraMode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (CameraModeCombo == null) return;
        _cameraMode = CameraModeCombo.SelectedIndex switch
        {
            1 => CameraMode.FollowPulse,
            2 => CameraMode.Orbit,
            3 => CameraMode.Overview,
            4 => CameraMode.RandomWalk,
            5 => CameraMode.RealBrain,
            _ => CameraMode.Free
        };

        // Reset attention state so mode starts fresh
        if (_cameraMode == CameraMode.RealBrain)
        {
            _attentionTarget = null;
            _attentionStartedAt = DateTime.UtcNow;
        }

        if (StatusText != null)
            StatusText.Text = $"Camera: {_cameraMode}";
    }

    private void FitOverview_Click(object sender, RoutedEventArgs e)
    {
        var b = ComputeBounds(_graphPhysics);
        _graphTarget = b.center;
        _graphDist = Math.Clamp(b.radius * 2.5, 6, 120);
        StatusText.Text = $"Fitted to graph bounds · radius {b.radius:F1}";
    }

    /// <summary>
    /// Climb one level up the cluster tree: find which bubble the
    /// camera target is inside, zoom out so that bubble fits the
    /// viewport. Repeat clicks walk up toward the root.
    /// </summary>
    // ═══════════════════════════════════════
    // BRAIN GRAPH SEARCH (TextBox + dropdown + fly-to)
    // ═══════════════════════════════════════

    private List<(string id, string title, int physicsIdx)> _lastSearchHits = [];

    /// <summary>Ctrl+F: switch to the BrainGraph view and focus the search box.</summary>
    private void FocusGraphSearch()
    {
        // If we're not on the graph view, simulate a nav click to switch
        if (BrainGraphView.Visibility != Visibility.Visible)
            Nav_Click(NavBrainGraph, new RoutedEventArgs());

        GraphSearchBox?.Focus();
        GraphSearchBox?.SelectAll();
    }

    private void GraphSearch_GotFocus(object s, RoutedEventArgs e)
    {
        // If there's already text, re-run the search
        if (!string.IsNullOrWhiteSpace(GraphSearchBox.Text))
            RunGraphSearch(GraphSearchBox.Text);
    }

    private void GraphSearch_TextChanged(object s, TextChangedEventArgs e)
    {
        var q = GraphSearchBox.Text;
        if (string.IsNullOrWhiteSpace(q))
        {
            GraphSearchResults.Visibility = Visibility.Collapsed;
            _lastSearchHits.Clear();
            return;
        }
        RunGraphSearch(q);
    }

    private void GraphSearch_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            GraphSearchBox.Text = "";
            GraphSearchResults.Visibility = Visibility.Collapsed;
            FullGraphViewport.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_lastSearchHits.Count > 0) FlyToSearchHit(0);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _lastSearchHits.Count > 0)
        {
            GraphSearchList.Focus();
            GraphSearchList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void GraphSearchList_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (GraphSearchList.SelectedIndex >= 0)
            FlyToSearchHit(GraphSearchList.SelectedIndex);
    }

    private void GraphSearchList_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GraphSearchList.SelectedIndex >= 0)
        {
            FlyToSearchHit(GraphSearchList.SelectedIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            GraphSearchBox.Text = "";
            GraphSearchResults.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void RunGraphSearch(string query)
    {
        _lastSearchHits.Clear();
        GraphSearchList.Items.Clear();

        // Prefer SQLite FTS5 (handles Thai via unicode61). Falls back to
        // in-memory title/tag/preview scan if storage isn't ready yet.
        List<SearchResult> hits;
        try
        {
            _storage ??= BrainStorageFactory.Create(_storageProvider, _vaultPath, _mySqlConnString);
            hits = _storage.Search(query, 25);
        }
        catch
        {
            hits = FallbackSearch(query, 25);
        }

        // Build physics-index mapping so we can lerp camera to the node
        var idToIdx = new Dictionary<string, int>(_graphPhysics.Nodes.Count);
        for (int i = 0; i < _graphPhysics.Nodes.Count; i++)
            idToIdx[_graphPhysics.Nodes[i].Id] = i;

        foreach (var h in hits)
        {
            if (!idToIdx.TryGetValue(h.NodeId, out var idx)) continue;
            _lastSearchHits.Add((h.NodeId, h.Title, idx));
            var cat = string.IsNullOrEmpty(h.Category) ? "" : $"  · {h.Category}";
            var display = $"{h.Title}{cat}";
            GraphSearchList.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = display,
                Padding = new Thickness(10, 6, 10, 6),
                Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 255))
            });
        }

        GraphSearchSummary.Text = _lastSearchHits.Count == 0
            ? $"No matches for \"{query}\""
            : $"{_lastSearchHits.Count} match·es · Enter to fly to first · ↓ to browse";
        GraphSearchResults.Visibility = Visibility.Visible;
    }

    private List<SearchResult> FallbackSearch(string query, int limit)
    {
        var ql = query.ToLowerInvariant();
        return _graph.Nodes
            .Select(n => new { n, score = ScoreForSearch(n, ql) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(limit)
            .Select(x => new SearchResult
            {
                NodeId = x.n.Id,
                Title = x.n.Title,
                RelativePath = Path.GetRelativePath(_vaultPath, x.n.FilePath).Replace("\\", "/"),
                Category = x.n.PrimaryCategory.ToString(),
                Score = x.score
            }).ToList();
    }

    private static double ScoreForSearch(KnowledgeNode n, string ql)
    {
        double s = 0;
        if (n.Title.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 3;
        if (n.Tags.Any(t => t.Contains(ql, StringComparison.OrdinalIgnoreCase))) s += 2;
        if (n.PrimaryCategory.ToString().Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1;
        return s;
    }

    /// <summary>
    /// Fly the camera onto the Nth search hit: lerp target to its
    /// position, shrink camDist, pulse the node briefly so it stands
    /// out, and close the search dropdown.
    /// </summary>
    private void FlyToSearchHit(int idx)
    {
        if (idx < 0 || idx >= _lastSearchHits.Count) return;
        var (id, title, pidx) = _lastSearchHits[idx];
        if (pidx < 0 || pidx >= _graphPhysics.Nodes.Count) return;

        var node = _graphPhysics.Nodes[pidx];
        _graphTarget = node.Position;
        _graphDist = Math.Max(3.0, node.Radius * 10);
        _cameraMode = CameraMode.Free;
        if (CameraModeCombo != null) CameraModeCombo.SelectedIndex = 0;
        _selectedNodeGraph = pidx;

        // Highlight pulse — shows up as bright ripple + aura for ~3s
        node.AccessIntensity = 1.0;
        node.LastAccessedAt = DateTime.UtcNow;

        GraphSearchResults.Visibility = Visibility.Collapsed;
        FullGraphViewport.Focus();

        ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot, GraphNodeContent, node);
        StatusText.Text = $"🎯 Flew to: {title}";
    }

    private void ZoomOutLevel_Click(object sender, RoutedEventArgs e)
    {
        var tree = _graphPhysics.ClusterTree;
        if (tree == null) return;

        // Find deepest cluster whose sphere contains the current target
        ClusterTree? bestContainer = null;
        WalkAllClusters(tree, c =>
        {
            if (c.IsLeaf || c == tree) return;
            var d = Distance(c.Center, _graphTarget);
            if (d < c.Radius && (bestContainer == null || c.Depth > bestContainer.Depth))
                bestContainer = c;
        });

        // Step up: parent of the cluster we're inside
        var up = bestContainer?.Parent ?? tree;
        _graphTarget = up.Center;
        _graphDist = Math.Clamp(up.Radius * 2.4, 6, 120);
        var label = up.Depth == 0 ? "Brain (root)" : up.Label;
        StatusText.Text = $"⬆ Zoomed out to: {label}";
    }

    private static void WalkAllClusters(ClusterTree t, Action<ClusterTree> visit)
    {
        visit(t);
        foreach (var c in t.Children) WalkAllClusters(c, visit);
    }

    private static double Distance(Point3D a, Point3D b)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y; var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // ═══════════════════════════════════════
    // IDENTITY & INDEXING
    // ═══════════════════════════════════════
    private void InitializeIdentity()
    {
        var dir = Path.GetDirectoryName(_identityPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(_identityPath))
            _identity = BrainIdentity.LoadFromFile(_identityPath);
        else
        {
            _identity = BrainIdentity.Generate(Environment.UserName + "'s Brain");
            _identity.SaveToFile(_identityPath);
        }

        BrainNameText.Text = _identity.DisplayName;
        BrainAddressText.Text = _identity.Address;
        FullAddressText.Text = _identity.Address;
    }

    private void IndexVault()
    {
        StatusText.Text = "Indexing vault...";
        if (!Directory.Exists(_vaultPath)) Directory.CreateDirectory(_vaultPath);
        _indexer.AutoLinker ??= new AutoLinker();
        _indexer.AutoLinker.Options.Enabled = _autoLinkEnabled;
        _indexer.AutoLinker.Options.Threshold = _autoLinkThreshold;
        _categories ??= new CategoryRegistry(_vaultPath);
        _indexer.CustomCategories = _categories;
        _graph = _indexer.IndexVault(_vaultPath);

        // Push into configured storage (SQLite by default — FTS5 search)
        try
        {
            _storage ??= BrainStorageFactory.Create(_storageProvider, _vaultPath, _mySqlConnString);
            _storage.UpsertGraph(_graph);
        }
        catch (Exception ex) { Debug.WriteLine($"Storage upsert failed: {ex.Message}"); }

        // Keep brain-export.json in sync so Claude (via MCP) sees the
        // SAME node IDs we rendered. Skipping this caused the pulse
        // LEDs to miss their targets because IDs drifted across
        // re-index runs. Now stable IDs + fresh export → always sync.
        string exportMsg;
        try
        {
            if (_identity == null)
                exportMsg = " · export SKIPPED (identity not ready)";
            else
            {
                var r = _exporter.Export(_vaultPath, _identity, _graph);
                exportMsg = $" · exported {r.NodeCount} nodes → brain-export.json";
            }
        }
        catch (Exception ex)
        {
            exportMsg = $" · EXPORT FAILED: {ex.Message}";
            Debug.WriteLine($"Export after index failed: {ex}");
            // File-logged so the user can inspect after the fact
            try
            {
                var logPath = Path.Combine(_vaultPath, ".obsidianx", "export-error.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] {ex}\n\n");
            }
            catch { }
        }

        var wikiEdges = _graph.Edges.Count(e => e.RelationType == "wiki-link");
        var autoEdges = _graph.Edges.Count - wikiEdges;
        StatusText.Text = $"Indexed {_graph.TotalNodes} nodes · {wikiEdges} wiki · {autoEdges} auto-links · storage: {_storage?.ProviderName ?? "File"}{exportMsg}";
    }

    private void CheckClaudeConnection()
    {
        _claude = new ClaudeIntegration(_vaultPath);
        var status = _claude.CheckConnection();
        if (status.IsConnected)
        {
            ClaudeStatusText.Text = "Connected to Claude";
            ClaudeStatusText.Foreground = (SolidColorBrush)FindResource("NeonGreenBrush");
            ClaudeDetailText.Text = $"CLAUDE.md active at {_vaultPath}";
            ClaudeViewStatus.Text = "Claude is connected to your brain vault";
        }
        else
        {
            ClaudeStatusText.Text = status.StatusMessage;
            ClaudeStatusText.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
            ClaudeDetailText.Text = "Click 'Connect to Claude' to set up";
            ClaudeViewStatus.Text = status.StatusMessage;
        }
    }

    private void UpdateUI()
    {
        StatNotes.Text = _graph.TotalNodes.ToString("N0");
        StatWords.Text = _graph.TotalWords.ToString("N0");
        StatLinks.Text = _graph.TotalEdges.ToString("N0");
        StatCategories.Text = _graph.ExpertiseMap.Count.ToString();
        VaultPathText.Text = _vaultPath;
        BuildExpertiseBars();
        PopulateVaultTree();
    }

    private void BuildExpertiseBars()
    {
        ExpertisePanel.Children.Clear();
        Color[] barColors =
        [
            Color.FromRgb(0, 240, 255), Color.FromRgb(139, 92, 246),
            Color.FromRgb(255, 0, 110), Color.FromRgb(0, 255, 136),
            Color.FromRgb(255, 184, 0), Color.FromRgb(255, 107, 107),
            Color.FromRgb(78, 205, 196), Color.FromRgb(168, 230, 207),
        ];

        int ci = 0;
        foreach (var (category, score) in _graph.ExpertiseMap.OrderByDescending(kv => kv.Value.Score).Take(8))
        {
            var color = barColors[ci++ % barColors.Length];
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            var header = new Grid();
            header.Children.Add(new TextBlock
            {
                Text = category.ToString().Replace("_", " / "),
                FontSize = 11,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Left
            });
            header.Children.Add(new TextBlock
            {
                Text = $"{score.Score:P0}",
                FontSize = 11,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = (FontFamily)FindResource("MonoFont")
            });
            panel.Children.Add(header);

            var barGrid = new Grid { Height = 4, Margin = new Thickness(0, 4, 0, 0) };
            barGrid.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = (SolidColorBrush)FindResource("SurfaceBrush") });
            barGrid.Children.Add(new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(4, score.Score * 300),
                Background = new LinearGradientBrush(color, Color.FromArgb(100, color.R, color.G, color.B), 0)
            });
            panel.Children.Add(barGrid);

            panel.Children.Add(new TextBlock
            {
                Text = $"{score.NoteCount} notes · {score.TotalWords:N0} words",
                FontSize = 9,
                Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            ExpertisePanel.Children.Add(panel);
        }

        if (_graph.ExpertiseMap.Count == 0)
        {
            ExpertisePanel.Children.Add(new TextBlock
            {
                Text = "No notes indexed yet.\nAdd .md files to your vault.",
                FontSize = 12,
                Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush"),
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    // ═══════════════════════════════════════
    // NAVIGATION
    // ═══════════════════════════════════════
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        foreach (var vn in _viewMap.Values)
        {
            var view = (UIElement?)FindName(vn);
            if (view != null) view.Visibility = Visibility.Collapsed;
        }
        if (_viewMap.TryGetValue(tag, out var tv))
        {
            var view = (UIElement?)FindName(tv);
            if (view != null) view.Visibility = Visibility.Visible;
        }

        Button[] navButtons = [NavDashboard, NavBrainGraph, NavNetwork, NavEditor, NavVault, NavSearch, NavClaude, NavGrowth, NavPeers, NavSharing, NavSettings];
        foreach (var nb in navButtons) nb.Style = (Style)FindResource("NavButton");
        btn.Style = (Style)FindResource("NavButtonActive");

        // Special rendering for specific views
        if (tag == "Growth") RenderGrowthChart();
        if (tag == "Peers") RefreshPeersList();
        if (tag == "Editor") RefreshBacklinks();
        if (tag == "Search") SearchBox.Focus();
        if (tag == "Network") _ = RefreshNetworkStats();
        if (tag == "Vault") RefreshVaultTree();
    }

    // ═══════════════════════════════════════
    // WINDOW CONTROLS
    // ═══════════════════════════════════════
    private void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save any unsaved editor work
        _mdEditor?.Save();

        // Persist settings
        SaveSettingsToFile();

        // Unsubscribe render loop
        CompositionTarget.Rendering -= OnRenderFrame;

        // Disconnect from network
        try { await _network.DisconnectAsync(); }
        catch (Exception ex) { Debug.WriteLine($"Disconnect error: {ex.Message}"); }
    }

    // ═══════════════════════════════════════
    // NETWORK STATS
    // ═══════════════════════════════════════
    private async Task RefreshNetworkStats()
    {
        if (!_network.IsConnected)
        {
            NetStatNodes.Text = "—";
            NetStatWords.Text = "—";
            NetStatShares.Text = "—";
            return;
        }

        try
        {
            var stats = await _network.GetNetworkStatsAsync();
            if (stats is Newtonsoft.Json.Linq.JObject obj)
            {
                NetworkPeerCount.Text = obj["TotalPeers"]?.ToString() ?? "0";
                NetStatNodes.Text = (obj["TotalKnowledge"]?.ToObject<int>() ?? 0).ToString("N0");
                NetStatWords.Text = (obj["TotalWords"]?.ToObject<int>() ?? 0).ToString("N0");

                var categories = obj["Categories"] as Newtonsoft.Json.Linq.JObject;
                NetStatShares.Text = categories?.Count.ToString() ?? "0";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Network stats error: {ex.Message}");
            StatusText.Text = "Could not fetch network stats";
        }
    }

    private void CopyAddress_Click(object s, MouseButtonEventArgs e)
    {
        Clipboard.SetText(_identity.Address);
        StatusText.Text = "Brain address copied!";
    }

    // ═══════════════════════════════════════
    // ACTIONS
    // ═══════════════════════════════════════
    private void ConnectClaude_Click(object s, RoutedEventArgs e)
    {
        _claude.GenerateClaudeMd(_graph, _identity);
        CheckClaudeConnection();
        StatusText.Text = "Claude connected! CLAUDE.md generated.";
        MessageBox.Show(
            $"CLAUDE.md generated at:\n{_vaultPath}\\CLAUDE.md\n\n" +
            "To use:\n1. Open terminal at vault folder\n2. Run: claude\n3. Claude reads your brain profile!\n\nStatus: SUCCESS",
            "ObsidianX — Claude Connected", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ReindexVault_Click(object s, RoutedEventArgs e)
    {
        IndexVault();
        _dashPhysics.LoadFromGraphDiff(_graph);
        _graphPhysics.LoadFromGraphDiff(_graph);
        // LoadFromGraph already auto-tunes + warmups — just a tiny kick
        var kick = _graph.TotalNodes > 20 ? 0.05 : 0.3;
        _dashPhysics.Disturb(kick);
        _graphPhysics.Disturb(kick);
        UpdateUI();
        StatusText.Text = $"Re-indexed: {_graph.TotalNodes} nodes, {_graph.TotalEdges} edges";
    }

    private void OpenObsidian_Click(object s, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"obsidian://open?path={Uri.EscapeDataString(_vaultPath)}")
                { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show("Could not open Obsidian. Please open it manually.", "ObsidianX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void JoinNetwork_Click(object s, RoutedEventArgs e)
    {
        if (_network.IsConnected) return;

        StatusText.Text = "Connecting to ObsidianX Network...";
        JoinNetworkBtn.IsEnabled = false;
        JoinNetworkBtn.Content = "Connecting...";

        var myInfo = new PeerInfo
        {
            BrainAddress = _identity.Address,
            DisplayName = _identity.DisplayName,
            PublicKey = _identity.PublicKey,
            ExpertiseScores = _graph.ExpertiseMap.ToDictionary(kv => kv.Key, kv => kv.Value.Score),
            TotalKnowledgeNodes = _graph.TotalNodes,
            TotalWords = _graph.TotalWords,
            JoinedAt = DateTime.UtcNow
        };

        var success = await _network.ConnectAsync(_serverUrl, myInfo);
        if (success)
        {
            JoinNetworkBtn.Content = "\u2705 Connected";
            LeaveNetworkBtn.Visibility = Visibility.Visible;
            StatusText.Text = "Connected to ObsidianX Network!";
        }
        else
        {
            JoinNetworkBtn.Content = "\U0001F310 Join ObsidianX Network";
            JoinNetworkBtn.IsEnabled = true;
            StatusText.Text = "Failed to connect. Is the server running?";
            MessageBox.Show(
                "Could not connect to ObsidianX Server.\n\n" +
                "Start the server first:\n  cd ObsidianX.Server && dotnet run\n\n" +
                $"Server URL: {_serverUrl}",
                "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void LeaveNetwork_Click(object s, RoutedEventArgs e)
    {
        try
        {
            await _network.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Disconnect error: {ex.Message}");
        }
        JoinNetworkBtn.Content = "\U0001F310 Join ObsidianX Network";
        JoinNetworkBtn.IsEnabled = true;
        LeaveNetworkBtn.Visibility = Visibility.Collapsed;
        StatusText.Text = "Disconnected from network";
    }

    private async void FindExperts_Click(object s, RoutedEventArgs e)
    {
        if (!_network.IsConnected)
        {
            MessageBox.Show("Connect to network first.", "Find Experts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var category = MatchCategoryCombo.SelectedItem is ComboBoxItem item
            ? Enum.Parse<KnowledgeCategory>(item.Tag.ToString()!)
            : KnowledgeCategory.Programming;

        List<MatchResult> results;
        try
        {
            results = await _network.FindExpertsAsync(new MatchRequest
            {
                RequesterAddress = _identity.Address,
                DesiredCategory = category,
                MinExpertiseScore = 0.1,
                MaxResults = 10
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {ex.Message}";
            return;
        }

        MatchResultsList.Children.Clear();
        if (results.Count == 0)
        {
            MatchResultsList.Children.Add(new TextBlock
            {
                Text = "No experts found for this category",
                FontSize = 11, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"), FontStyle = FontStyles.Italic
            });
            return;
        }

        foreach (var match in results)
        {
            var card = new Border
            {
                Background = (SolidColorBrush)FindResource("SurfaceBrush"),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = match.Peer.DisplayName,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush")
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{match.MatchReason} · Score: {match.MatchScore:P0} · {match.Peer.TotalKnowledgeNodes} nodes",
                FontSize = 10, Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush")
            });
            grid.Children.Add(info);

            var shareBtn = new Button
            {
                Content = "Request Share", Style = (Style)FindResource("NeonButton"),
                Padding = new Thickness(8, 4, 8, 4), FontSize = 10,
                Tag = match.Peer.BrainAddress
            };
            shareBtn.Click += RequestShare_Click;
            Grid.SetColumn(shareBtn, 1);
            grid.Children.Add(shareBtn);

            card.Child = grid;
            MatchResultsList.Children.Add(card);
        }
    }

    private async void RequestShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string targetAddress) return;
        try
        {
            await _network.RequestShareAsync(new ShareRequest
            {
                FromAddress = _identity.Address,
                ToAddress = targetAddress,
                NodeTitle = "Knowledge Exchange",
                Category = KnowledgeCategory.Other,
                WordCount = (int)_graph.TotalWords,
                Signature = _identity.Sign(targetAddress)
            });
            var shortAddr = targetAddress.Length > 20 ? targetAddress[..20] + "..." : targetAddress;
            _shareHistory.Add($"[SENT] Request to {shortAddr}");
            StatusText.Text = "Share request sent!";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Share request failed: {ex.Message}";
        }
    }

    private void ExportStats_Click(object s, RoutedEventArgs e)
    {
        var path = Path.Combine(_vaultPath, ".obsidianx", "brain_stats.json");
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            identity = new { _identity.Address, _identity.DisplayName, _identity.CreatedAt },
            stats = new { _graph.TotalNodes, _graph.TotalEdges, _graph.TotalWords },
            expertise = _graph.ExpertiseMap.ToDictionary(kv => kv.Key.ToString(),
                kv => new { kv.Value.Score, kv.Value.NoteCount, kv.Value.TotalWords })
        }, Newtonsoft.Json.Formatting.Indented));
        StatusText.Text = $"Stats exported: {path}";
    }

    private async void AskClaude_Click(object s, RoutedEventArgs e)
    {
        var q = ClaudeInput.Text.Trim();
        if (string.IsNullOrEmpty(q)) return;

        var backend = (AiBackendCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ollama";
        var model = (AiModelCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "llama3.2:3b";

        ClaudeOutput.Text += $"\n\n> YOU: {q}\n\n{backend}/{model}: ";
        ClaudeInput.Text = "";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var startLen = ClaudeOutput.Text.Length;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                message = q, backend, model, stream = true
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, _serverUrl.Replace("/brain-hub", "") + "/api/ai/stream")
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var sr = new StreamReader(stream);
            string? line;
            while ((line = await sr.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var json = line[6..];
                try
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                    var delta = obj["delta"]?.ToString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        ClaudeOutput.Text += delta;
                    }
                    if (obj["done"]?.ToObject<bool>() == true) break;
                    var err = obj["error"]?.ToString();
                    if (!string.IsNullOrEmpty(err))
                    {
                        ClaudeOutput.Text += $"\n[error: {err}]";
                        break;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            ClaudeOutput.Text += $"\n[Error: {ex.Message}]";
        }
        sw.Stop();
        if (AiLatencyText != null)
            AiLatencyText.Text = $"{sw.ElapsedMilliseconds}ms · {ClaudeOutput.Text.Length - startLen}c";
    }

    private void ClaudeInput_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) AskClaude_Click(s, e); }

    // ═══════════════════════════════════════
    // AI HUB — backend + model selector
    // ═══════════════════════════════════════

    private async void RefreshAiBackends_Click(object s, RoutedEventArgs e) => await LoadAiBackends();

    private async Task LoadAiBackends()
    {
        if (AiBackendCombo == null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = _serverUrl.Replace("/brain-hub", "") + "/api/ai/backends";
            var json = await http.GetStringAsync(url);
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);

            AiBackendCombo.Items.Clear();
            AiModelCombo.Items.Clear();
            var backends = root["backends"] as Newtonsoft.Json.Linq.JArray ?? [];
            foreach (var be in backends)
            {
                var name = be["name"]?.ToString() ?? "";
                var avail = be["available"]?.ToObject<bool>() ?? false;
                AiBackendCombo.Items.Add(new ComboBoxItem
                {
                    Content = name,
                    Tag = be["models"],
                    Foreground = avail
                        ? (SolidColorBrush)FindResource("TextPrimaryBrush")
                        : (SolidColorBrush)FindResource("TextMutedBrush")
                });
            }
            if (AiBackendCombo.Items.Count > 0) AiBackendCombo.SelectedIndex = 0;

            if (ClaudeViewStatus != null)
                ClaudeViewStatus.Text = $"{backends.Count} backend(s) available · default model: {root["defaultModel"]}";
        }
        catch (Exception ex)
        {
            if (ClaudeViewStatus != null)
                ClaudeViewStatus.Text = $"AI Hub unreachable: {ex.Message}. Start the server.";
        }
    }

    private void AiBackend_Changed(object s, SelectionChangedEventArgs e)
    {
        if (AiBackendCombo?.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not Newtonsoft.Json.Linq.JArray models) return;
        AiModelCombo.Items.Clear();
        foreach (var m in models)
        {
            AiModelCombo.Items.Add(new ComboBoxItem { Content = m.ToString() });
        }
        if (AiModelCombo.Items.Count > 0) AiModelCombo.SelectedIndex = 0;
    }

    // ═══════════════════════════════════════
    // CLOUD AI BACKEND KEYS
    // ═══════════════════════════════════════

    private async void SaveAiKeys_Click(object s, RoutedEventArgs e)
    {
        var payload = new Newtonsoft.Json.Linq.JObject();
        var nim = NimKeyBox.Password?.Trim() ?? "";
        var orou = OpenRouterKeyBox.Password?.Trim() ?? "";
        var ds = DeepSeekKeyBox.Password?.Trim() ?? "";
        if (!string.IsNullOrEmpty(nim)) payload["nim_api_key"] = nim;
        if (!string.IsNullOrEmpty(orou)) payload["openrouter_api_key"] = orou;
        if (!string.IsNullOrEmpty(ds)) payload["deepseek_api_key"] = ds;

        if (payload.Count == 0)
        {
            AiKeysStatus.Text = "No keys entered.";
            return;
        }

        try
        {
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, AiServerBase + "/api/ai/keys")
            {
                Content = new StringContent(payload.ToString(), System.Text.Encoding.UTF8, "application/json")
            };
            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                AiKeysStatus.Text = $"✅ Saved {payload.Count} key(s). Click 'Refresh backends' to reload.";
                // Clear inputs so key isn't shown back
                NimKeyBox.Password = "";
                OpenRouterKeyBox.Password = "";
                DeepSeekKeyBox.Password = "";
                await RefreshAiKeyStatus();
            }
            else
            {
                AiKeysStatus.Text = $"❌ Failed: {await resp.Content.ReadAsStringAsync()}";
            }
        }
        catch (Exception ex) { AiKeysStatus.Text = $"❌ Error: {ex.Message}"; }
    }

    private async void RefreshAfterKeys_Click(object s, RoutedEventArgs e)
    {
        await LoadAiBackends();
        await RefreshAiKeyStatus();
        AiKeysStatus.Text = "Backends reloaded. Check the Backend dropdown in Claude view.";
    }

    private async Task RefreshAiKeyStatus()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = await http.GetStringAsync(AiServerBase + "/api/ai/keys/status");
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);

            NimKeyStatus.Text = IsKeySet(root, "nim_api_key") ? "✓ configured" : "(not set)";
            OpenRouterKeyStatus.Text = IsKeySet(root, "openrouter_api_key") ? "✓ configured" : "(not set)";
            DeepSeekKeyStatus.Text = IsKeySet(root, "deepseek_api_key") ? "✓ configured" : "(not set)";

            var greenBrush = (SolidColorBrush)FindResource("NeonGreenBrush");
            var mutedBrush = (SolidColorBrush)FindResource("TextMutedBrush");
            NimKeyStatus.Foreground = IsKeySet(root, "nim_api_key") ? greenBrush : mutedBrush;
            OpenRouterKeyStatus.Foreground = IsKeySet(root, "openrouter_api_key") ? greenBrush : mutedBrush;
            DeepSeekKeyStatus.Foreground = IsKeySet(root, "deepseek_api_key") ? greenBrush : mutedBrush;
        }
        catch { /* server might not be up */ }
    }

    private static bool IsKeySet(Newtonsoft.Json.Linq.JObject root, string field) =>
        root[field]?.ToObject<bool>() == true;

    // ═══════════════════════════════════════
    // REDIRECT CLAUDE DESKTOP → LOCAL AI
    // ═══════════════════════════════════════

    private DispatcherTimer? _redirectStatsTimer;
    private const string RedirectEnvBaseUrl = "ANTHROPIC_BASE_URL";
    private const string RedirectEnvApiKey  = "ANTHROPIC_API_KEY";

    private void InitRedirectToggle()
    {
        if (RedirectToggle == null) return;
        // Reflect current env-var state (User scope)
        var curBase = Environment.GetEnvironmentVariable(RedirectEnvBaseUrl, EnvironmentVariableTarget.User);
        var active = !string.IsNullOrEmpty(curBase)
                  && curBase.Contains("localhost:5142", StringComparison.OrdinalIgnoreCase);
        RedirectToggle.IsChecked = active;
        UpdateRedirectStatusText(active);

        // Start live-traffic poll
        _redirectStatsTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _redirectStatsTimer.Tick -= RedirectStatsTimer_Tick;
        _redirectStatsTimer.Tick += RedirectStatsTimer_Tick;
        _redirectStatsTimer.Start();
    }

    private void UpdateRedirectStatusText(bool active)
    {
        if (RedirectStatusText == null) return;
        RedirectStatusText.Text = active
            ? "✅ ON — Claude Desktop routes to http://localhost:5142 (local Ollama + brain). Restart Claude Desktop after toggling."
            : "OFF — Claude Desktop talks to api.anthropic.com (cloud)";
    }

    private void RedirectToggle_Checked(object s, RoutedEventArgs e)
    {
        Environment.SetEnvironmentVariable(RedirectEnvBaseUrl, AiServerBase, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(RedirectEnvApiKey, "local-ollama", EnvironmentVariableTarget.User);
        UpdateRedirectStatusText(true);
        MessageBox.Show(
            $"Environment variables set at User scope:\n\n" +
            $"  {RedirectEnvBaseUrl} = {AiServerBase}\n" +
            $"  {RedirectEnvApiKey} = local-ollama\n\n" +
            "Restart Claude Desktop to route through this server.\n" +
            "Claude Desktop will use local Ollama with your brain as context.",
            "Redirect enabled", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RedirectToggle_Unchecked(object s, RoutedEventArgs e)
    {
        Environment.SetEnvironmentVariable(RedirectEnvBaseUrl, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(RedirectEnvApiKey, null, EnvironmentVariableTarget.User);
        UpdateRedirectStatusText(false);
        MessageBox.Show(
            "Environment variables cleared. Restart Claude Desktop to go back to api.anthropic.com.",
            "Redirect disabled", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ToggleRedirectTraffic_Click(object s, RoutedEventArgs e)
    {
        if (RedirectTrafficPanel == null) return;
        RedirectTrafficPanel.Visibility =
            RedirectTrafficPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ResetRedirectStats_Click(object s, RoutedEventArgs e)
    {
        try
        {
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, AiServerBase + "/api/ai/stats/router/reset");
            await http.SendAsync(req);
            await PollRedirectStats();
        }
        catch { }
    }

    private async void RedirectStatsTimer_Tick(object? sender, EventArgs e) => await PollRedirectStats();

    private async Task PollRedirectStats()
    {
        if (RedirectTrafficText == null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var json = await http.GetStringAsync(AiServerBase + "/api/ai/stats/router");
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            var total = root["totalRequests"]?.ToObject<long>() ?? 0;
            var bIn = root["bytesIn"]?.ToObject<long>() ?? 0;
            var bOut = root["bytesOut"]?.ToObject<long>() ?? 0;
            RedirectTrafficText.Text = $"Traffic: {total:N0} requests  ·  {FormatBytes(bIn)} in  ·  {FormatBytes(bOut)} out";

            if (RedirectTrafficPanel != null && RedirectTrafficPanel.Visibility == Visibility.Visible && RedirectTrafficList != null)
            {
                RedirectTrafficList.Items.Clear();
                foreach (var ev in root["recent"] as Newtonsoft.Json.Linq.JArray ?? [])
                {
                    var ts = ev["ts"]?.ToObject<DateTime>() ?? default;
                    var path = ev["path"]?.ToString() ?? "";
                    var status = ev["status"]?.ToObject<int>() ?? 0;
                    var elapsed = ev["elapsedMs"]?.ToObject<long>() ?? 0;
                    var sIn = ev["bytesIn"]?.ToObject<long>() ?? 0;
                    var sOut = ev["bytesOut"]?.ToObject<long>() ?? 0;
                    RedirectTrafficList.Items.Add(
                        $"{ts.ToLocalTime():HH:mm:ss}  {status,3}  {elapsed,5}ms  {FormatBytes(sIn),7} → {FormatBytes(sOut),-7}  {path}");
                }
            }
        }
        catch { /* server might be down momentarily */ }
    }

    private static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):F1}GB"
      : b >= 1L << 20 ? $"{b / (double)(1L << 20):F1}MB"
      : b >= 1L << 10 ? $"{b / 1024.0:F1}KB"
      : $"{b}B";

    // ═══════════════════════════════════════
    // OLLAMA MODEL MANAGER
    // ═══════════════════════════════════════

    private string AiServerBase => _serverUrl.Replace("/brain-hub", "");

    private async void ToggleModelManager_Click(object s, RoutedEventArgs e)
    {
        if (ModelManagerPanel == null) return;
        ModelManagerPanel.Visibility =
            ModelManagerPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        if (ModelManagerPanel.Visibility == Visibility.Visible)
            await RefreshModelManager();
    }

    private async Task RefreshModelManager()
    {
        if (InstalledModelsList == null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync(AiServerBase + "/api/ai/models");
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);

            InstalledModelsList.Items.Clear();
            foreach (var m in root["installed"] as Newtonsoft.Json.Linq.JArray ?? [])
            {
                var name = m["Name"]?.ToString() ?? m["name"]?.ToString();
                var size = m["SizeHuman"]?.ToString() ?? m["sizeHuman"]?.ToString();
                var param = m["ParameterSize"]?.ToString() ?? m["parameterSize"]?.ToString();
                var fam = m["Family"]?.ToString() ?? m["family"]?.ToString();
                InstalledModelsList.Items.Add($"{name,-32}  {param,-6}  {fam,-10}  {size}");
            }

            var running = root["running"] as Newtonsoft.Json.Linq.JArray ?? [];
            RunningModelsText.Text = running.Count == 0
                ? "(no models warm — first call will load from disk)"
                : string.Join(", ", running.Select(r => r["Name"]?.ToString() ?? r["name"]?.ToString()));
        }
        catch (Exception ex)
        {
            ModelPullStatus.Text = $"Failed to load models: {ex.Message}";
        }
    }

    private async void UseSelectedModel_Click(object s, RoutedEventArgs e)
    {
        if (InstalledModelsList?.SelectedItem is not string line) return;
        var name = line.Split(' ')[0];
        // Find it in the combo and select
        for (int i = 0; i < AiModelCombo.Items.Count; i++)
        {
            if (AiModelCombo.Items[i] is ComboBoxItem item && item.Content?.ToString() == name)
            {
                AiModelCombo.SelectedIndex = i;
                StatusText.Text = $"Default model set to {name}";
                return;
            }
        }
        // Not in combo — add and select
        AiModelCombo.Items.Add(new ComboBoxItem { Content = name });
        AiModelCombo.SelectedIndex = AiModelCombo.Items.Count - 1;
        await Task.CompletedTask;
    }

    private async void RemoveModel_Click(object s, RoutedEventArgs e)
    {
        if (InstalledModelsList?.SelectedItem is not string line) return;
        var name = line.Split(' ')[0];
        var ok = MessageBox.Show($"Remove model '{name}' from disk?",
            "Confirm delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        try
        {
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Delete,
                AiServerBase + $"/api/ai/models/{Uri.EscapeDataString(name)}");
            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                ModelPullStatus.Text = $"Removed {name}";
                await RefreshModelManager();
                await LoadAiBackends();
            }
            else ModelPullStatus.Text = $"Delete failed: {await resp.Content.ReadAsStringAsync()}";
        }
        catch (Exception ex) { ModelPullStatus.Text = $"Delete error: {ex.Message}"; }
    }

    private async void PullModel_Click(object s, RoutedEventArgs e)
    {
        var name = ModelPullBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        ModelPullProgress.Visibility = Visibility.Visible;
        ModelPullProgress.Value = 0;
        ModelPullStatus.Text = $"Pulling {name}…";
        PullModelBtn.IsEnabled = false;

        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new { name });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                AiServerBase + "/api/ai/models/pull")
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var sr = new StreamReader(stream);
            string? line;
            while ((line = await sr.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                try
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(line[6..]);
                    var status = obj["Status"]?.ToString() ?? obj["status"]?.ToString() ?? "";
                    var total = obj["Total"]?.ToObject<long>() ?? obj["total"]?.ToObject<long>() ?? 0;
                    var done = obj["Completed"]?.ToObject<long>() ?? obj["completed"]?.ToObject<long>() ?? 0;
                    if (total > 0) ModelPullProgress.Value = 100.0 * done / total;
                    ModelPullStatus.Text = total > 0
                        ? $"{status} · {done / (1024.0 * 1024):F0} / {total / (1024.0 * 1024):F0} MB"
                        : status;
                    if (status == "done" || status.Contains("success")) break;
                }
                catch { }
            }
            ModelPullStatus.Text = $"✅ Pulled {name}";
            await RefreshModelManager();
            await LoadAiBackends();
        }
        catch (Exception ex)
        {
            ModelPullStatus.Text = $"Pull failed: {ex.Message}";
        }
        finally
        {
            ModelPullProgress.Visibility = Visibility.Collapsed;
            PullModelBtn.IsEnabled = true;
        }
    }

    private void PopulateVaultTree()
    {
        VaultTree.Items.Clear();
        if (!Directory.Exists(_vaultPath)) return;
        var root = new TreeViewItem
        {
            Header = Path.GetFileName(_vaultPath),
            IsExpanded = true,
            Foreground = (SolidColorBrush)FindResource("NeonCyanBrush"),
            FontWeight = FontWeights.SemiBold
        };
        AddDirToTree(root, _vaultPath);
        VaultTree.Items.Add(root);
    }

    private void AddDirToTree(TreeViewItem parent, string path)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;
                var item = new TreeViewItem { Header = $"\U0001F4C1 {name}", Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush") };
                AddDirToTree(item, dir);
                parent.Items.Add(item);
            }
            foreach (var file in Directory.GetFiles(path, "*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var node = _graph.Nodes.FirstOrDefault(n => n.Title == name);
                var color = node != null ? new SolidColorBrush(GetCategoryColor(node.PrimaryCategory))
                    : (SolidColorBrush)FindResource("TextPrimaryBrush");
                parent.Items.Add(new TreeViewItem { Header = $"\U0001F4C4 {name}", Foreground = color, Tag = file });
            }
        }
        catch (UnauthorizedAccessException) { /* Skip folders we can't read */ }
        catch (IOException ex) { Debug.WriteLine($"Tree scan error: {ex.Message}"); }
    }

    /// <summary>
    /// Node color priority: user's custom category color → built-in
    /// category color. Lets notes that scored highest on a user-defined
    /// subject paint in the color the user picked for that subject.
    /// </summary>
    private Color ResolveNodeColor(PhysicsNode node)
    {
        if (!string.IsNullOrEmpty(node.CustomCategoryId) && _categories != null)
        {
            var cat = _categories.FindById(node.CustomCategoryId);
            if (cat != null && TryParseColor(cat.ColorHex, out var c)) return c;
        }
        return GetCategoryColor(node.Category);
    }

    /// <summary>Cluster bubbles: if most members share a custom category, paint with it.</summary>
    private Color ResolveBubbleColor(ClusterTree bubble)
    {
        if (_categories == null) return GetCategoryColor(bubble.DominantCategory);

        // Tally custom categories among descendant leaves
        var counts = new Dictionary<string, int>();
        int totalLeaves = 0;
        CountLeafCustomCats(bubble, counts, ref totalLeaves);
        if (counts.Count > 0 && totalLeaves > 0)
        {
            var top = counts.OrderByDescending(kv => kv.Value).First();
            if (top.Value * 2 >= totalLeaves)   // simple majority
            {
                var cat = _categories.FindById(top.Key);
                if (cat != null && TryParseColor(cat.ColorHex, out var c)) return c;
            }
        }
        return GetCategoryColor(bubble.DominantCategory);
    }

    private static void CountLeafCustomCats(ClusterTree t, Dictionary<string, int> counts, ref int total)
    {
        if (t.IsLeaf)
        {
            total++;
            var id = t.Leaf!.CustomCategoryId;
            if (!string.IsNullOrEmpty(id))
                counts[id] = counts.GetValueOrDefault(id) + 1;
            return;
        }
        foreach (var c in t.Children) CountLeafCustomCats(c, counts, ref total);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            var obj = ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex);
            if (obj is Color c) { color = c; return true; }
        }
        catch (FormatException) { }
        return false;
    }

    /// <summary>
    /// Shift a category color slightly based on community id so visually
    /// adjacent clusters are still distinguishable while the category
    /// scheme remains dominant.
    /// </summary>
    private static Color TintByCommunity(Color baseColor, int communityId)
    {
        // Golden-angle hue rotation — consistent per community id
        var phase = (communityId * 137.5) % 360.0;
        var shift = (int)(Math.Sin(phase * Math.PI / 180.0) * 30);

        int r = Math.Clamp(baseColor.R + shift, 20, 255);
        int g = Math.Clamp(baseColor.G - shift / 2, 20, 255);
        int b = Math.Clamp(baseColor.B + shift / 3, 20, 255);
        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }

    private static Color GetCategoryColor(KnowledgeCategory cat) => cat switch
    {
        KnowledgeCategory.Programming => Color.FromRgb(0, 240, 255),
        KnowledgeCategory.AI_MachineLearning => Color.FromRgb(139, 92, 246),
        KnowledgeCategory.Blockchain_Web3 => Color.FromRgb(255, 184, 0),
        KnowledgeCategory.Science => Color.FromRgb(0, 255, 136),
        KnowledgeCategory.Design_Art => Color.FromRgb(255, 0, 110),
        KnowledgeCategory.Security_Crypto => Color.FromRgb(255, 70, 70),
        KnowledgeCategory.Web_Development => Color.FromRgb(78, 205, 196),
        KnowledgeCategory.DataScience => Color.FromRgb(168, 230, 207),
        KnowledgeCategory.Business_Finance => Color.FromRgb(255, 184, 0),
        KnowledgeCategory.Health_Medicine => Color.FromRgb(255, 150, 150),
        _ => Color.FromRgb(100, 100, 180)
    };

    // ═══════════════════════════════════════
    // NETWORK EVENT HANDLERS
    // ═══════════════════════════════════════
    private void OnNetworkStatus(string status)
    {
        NetworkStatusText.Text = status;
        if (status == "Connected")
        {
            NetworkDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
            StatusDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
        }
        else if (status == "Disconnected")
        {
            NetworkDot.Fill = (SolidColorBrush)FindResource("TextMutedBrush");
            StatusDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
        }
        else
        {
            NetworkDot.Fill = (SolidColorBrush)FindResource("NeonPinkBrush");
        }
    }

    private void OnPeerCountChanged(int count)
    {
        PeerCountText.Text = $"{count} peer{(count != 1 ? "s" : "")} connected";
        NetworkPeerCount.Text = count.ToString();
        PeersSubtitle.Text = $"{count} brain{(count != 1 ? "s" : "")} online";
    }

    private void OnPeerJoined(PeerInfo peer)
    {
        StatusText.Text = $"Peer joined: {peer.DisplayName}";
        RefreshPeersList();
    }

    private void OnPeerLeft(string address)
    {
        StatusText.Text = $"Peer left: {address[..20]}...";
        RefreshPeersList();
    }

    private void OnShareRequested(ShareRequest request)
    {
        _incomingShares.Add(request);
        RefreshSharingView();
        StatusText.Text = $"Incoming share request from {request.FromAddress[..20]}...";
    }

    private void OnShareResponse(string fromAddr, bool accepted, string title)
    {
        var status = accepted ? "ACCEPTED" : "REJECTED";
        _shareHistory.Add($"[{status}] {title} from {fromAddr[..20]}...");
        RefreshSharingView();
        StatusText.Text = $"Share {status.ToLower()}: {title}";
    }

    // ═══════════════════════════════════════
    // PEERS VIEW
    // ═══════════════════════════════════════
    private void RefreshPeers_Click(object s, RoutedEventArgs e) => RefreshPeersList();

    private void RefreshPeersList()
    {
        PeersList.Children.Clear();
        var peers = _network.Peers;

        if (peers.Count == 0)
        {
            PeersList.Children.Add(new TextBlock
            {
                Text = _network.IsConnected ? "No other peers online yet" : "Join the network to see peers",
                FontSize = 13, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontStyle = FontStyles.Italic, Margin = new Thickness(0, 12, 0, 0)
            });
            return;
        }

        foreach (var peer in peers)
        {
            var card = new Border
            {
                Background = (SolidColorBrush)FindResource("SurfaceBrush"),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0, 240, 255)),
                BorderThickness = new Thickness(1)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Avatar
            var avatar = new System.Windows.Shapes.Ellipse
            {
                Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0),
                Fill = new LinearGradientBrush(
                    Color.FromRgb(0, 240, 255), Color.FromRgb(139, 92, 246), 45)
            };
            grid.Children.Add(avatar);

            // Info
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(info, 1);
            info.Children.Add(new TextBlock
            {
                Text = peer.DisplayName,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush")
            });
            info.Children.Add(new TextBlock
            {
                Text = peer.BrainAddress,
                FontSize = 9, FontFamily = (FontFamily)FindResource("MonoFont"),
                Foreground = (SolidColorBrush)FindResource("NeonCyanBrush")
            });
            // Top expertise
            var topExp = peer.ExpertiseScores.OrderByDescending(kv => kv.Value).Take(3)
                .Select(kv => kv.Key.ToString().Replace("_", "/"));
            info.Children.Add(new TextBlock
            {
                Text = $"{peer.TotalKnowledgeNodes} nodes · {string.Join(", ", topExp)}",
                FontSize = 10, Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            grid.Children.Add(info);

            // Status dot
            var statusDot = new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center,
                Fill = peer.Status == PeerStatus.Online
                    ? (SolidColorBrush)FindResource("NeonGreenBrush")
                    : (SolidColorBrush)FindResource("TextMutedBrush")
            };
            Grid.SetColumn(statusDot, 2);
            grid.Children.Add(statusDot);

            card.Child = grid;
            PeersList.Children.Add(card);
        }
    }

    // ═══════════════════════════════════════
    // SHARING VIEW
    // ═══════════════════════════════════════
    private void RefreshSharingView()
    {
        // Incoming requests
        IncomingSharesList.Children.Clear();
        var pending = _incomingShares.Where(r => r.Status == ShareStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            IncomingSharesList.Children.Add(new TextBlock
            {
                Text = "No incoming requests",
                FontSize = 12, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"), FontStyle = FontStyles.Italic
            });
        }
        else
        {
            foreach (var req in pending)
            {
                var card = new Border
                {
                    Background = (SolidColorBrush)FindResource("SurfaceBrush"),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                grid.Children.Add(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = req.NodeTitle, FontSize = 12, FontWeight = FontWeights.SemiBold,
                            Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush") },
                        new TextBlock { Text = $"From: {req.FromAddress[..20]}... · {req.Category}",
                            FontSize = 10, Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush") }
                    }
                });

                var acceptBtn = new Button
                {
                    Content = "\u2705 Accept", Style = (Style)FindResource("NeonButtonFilled"),
                    Padding = new Thickness(8, 4, 8, 4), FontSize = 10, Tag = req.FromAddress,
                    Margin = new Thickness(8, 0, 4, 0)
                };
                acceptBtn.Click += async (_, _) =>
                {
                    await _network.RespondToShareAsync(req.FromAddress, true);
                    req.Status = ShareStatus.Accepted;
                    _shareHistory.Add($"[ACCEPTED] {req.NodeTitle} from {req.FromAddress[..20]}...");
                    RefreshSharingView();
                };
                Grid.SetColumn(acceptBtn, 1);
                grid.Children.Add(acceptBtn);

                var rejectBtn = new Button
                {
                    Content = "\u274C Reject", Style = (Style)FindResource("NeonButton"),
                    Padding = new Thickness(8, 4, 8, 4), FontSize = 10,
                    Foreground = (SolidColorBrush)FindResource("DangerBrush"),
                    BorderBrush = (SolidColorBrush)FindResource("DangerBrush")
                };
                rejectBtn.Click += async (_, _) =>
                {
                    await _network.RespondToShareAsync(req.FromAddress, false);
                    req.Status = ShareStatus.Rejected;
                    _shareHistory.Add($"[REJECTED] {req.NodeTitle} from {req.FromAddress[..20]}...");
                    RefreshSharingView();
                };
                Grid.SetColumn(rejectBtn, 2);
                grid.Children.Add(rejectBtn);

                card.Child = grid;
                IncomingSharesList.Children.Add(card);
            }
        }

        // History
        ShareHistoryList.Children.Clear();
        if (_shareHistory.Count == 0)
        {
            ShareHistoryList.Children.Add(new TextBlock
            {
                Text = "No sharing history yet",
                FontSize = 12, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"), FontStyle = FontStyles.Italic
            });
        }
        else
        {
            foreach (var entry in _shareHistory.AsEnumerable().Reverse().Take(20))
            {
                ShareHistoryList.Children.Add(new TextBlock
                {
                    Text = entry, FontSize = 11,
                    Foreground = entry.Contains("ACCEPTED")
                        ? (SolidColorBrush)FindResource("NeonGreenBrush")
                        : entry.Contains("REJECTED")
                            ? (SolidColorBrush)FindResource("DangerBrush")
                            : (SolidColorBrush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }
        }
    }

    // ═══════════════════════════════════════
    // KNOWLEDGE GROWTH CHART
    // ═══════════════════════════════════════
    private void RenderGrowthChart()
    {
        GrowthCanvas.Children.Clear();
        GrowthLegend.Children.Clear();

        var expertise = _graph.ExpertiseMap.OrderByDescending(kv => kv.Value.Score).Take(10).ToList();
        if (expertise.Count == 0)
        {
            GrowthCanvas.Children.Add(new TextBlock
            {
                Text = "Add notes to your vault to see growth data",
                FontSize = 14, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontStyle = FontStyles.Italic
            });
            return;
        }

        // Wait for canvas to have size
        GrowthCanvas.Dispatcher.InvokeAsync(() =>
        {
            var w = GrowthCanvas.ActualWidth;
            var h = GrowthCanvas.ActualHeight;
            if (w < 100 || h < 100) { w = 600; h = 300; }

            double barWidth = Math.Min(60, (w - 40) / expertise.Count - 8);
            double maxScore = expertise.Max(e => e.Value.Score);
            if (maxScore < 0.01) maxScore = 1;

            for (int i = 0; i < expertise.Count; i++)
            {
                var (cat, score) = expertise[i];
                var color = GetCategoryColor(cat);
                double barH = (score.Score / maxScore) * (h - 60);
                double x = 20 + i * (barWidth + 8);

                // Bar
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = barWidth, Height = barH,
                    RadiusX = 4, RadiusY = 4,
                    Fill = new LinearGradientBrush(color, Color.FromArgb(100, color.R, color.G, color.B), 90)
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, h - 40 - barH);
                GrowthCanvas.Children.Add(rect);

                // Score label on top
                var label = new TextBlock
                {
                    Text = $"{score.Score:P0}", FontSize = 9,
                    Foreground = new SolidColorBrush(color),
                    FontFamily = (FontFamily)FindResource("MonoFont")
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, h - 44 - barH);
                GrowthCanvas.Children.Add(label);

                // Category label at bottom
                var catLabel = new TextBlock
                {
                    Text = cat.ToString().Replace("_", "\n").Replace("MachineLearning", "ML"),
                    FontSize = 8, Width = barWidth, TextAlignment = TextAlignment.Center,
                    Foreground = (SolidColorBrush)FindResource("TextMutedBrush"), TextWrapping = TextWrapping.Wrap
                };
                Canvas.SetLeft(catLabel, x);
                Canvas.SetTop(catLabel, h - 36);
                GrowthCanvas.Children.Add(catLabel);

                // Notes count inside bar
                if (barH > 20)
                {
                    var countLabel = new TextBlock
                    {
                        Text = $"{score.NoteCount}", FontSize = 10, FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White, Width = barWidth, TextAlignment = TextAlignment.Center
                    };
                    Canvas.SetLeft(countLabel, x);
                    Canvas.SetTop(countLabel, h - 40 - barH + 4);
                    GrowthCanvas.Children.Add(countLabel);
                }

                // Legend
                var legendItem = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
                legendItem.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 4, 0) });
                legendItem.Children.Add(new TextBlock
                {
                    Text = $"{cat.ToString().Replace("_", "/")} ({score.TotalWords:N0}w)",
                    FontSize = 9, Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush")
                });
                GrowthLegend.Children.Add(legendItem);
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ═══════════════════════════════════════
    // SETTINGS
    // ═══════════════════════════════════════
    private void PopulateSettings()
    {
        SettingsBrainName.Text = _identity.DisplayName;
        SettingsBrainAddress.Text = _identity.Address;
        SettingsVaultPath.Text = _vaultPath;
        SettingsServerUrl.Text = _serverUrl;
    }

    private void PopulateMatchCategories()
    {
        MatchCategoryCombo.Items.Clear();
        foreach (var cat in Enum.GetValues<KnowledgeCategory>())
        {
            MatchCategoryCombo.Items.Add(new ComboBoxItem
            {
                Content = cat.ToString().Replace("_", " / "),
                Tag = cat.ToString()
            });
        }
        MatchCategoryCombo.SelectedIndex = 0;
    }

    private void SaveBrainName_Click(object s, RoutedEventArgs e)
    {
        var newName = SettingsBrainName.Text.Trim();
        if (string.IsNullOrEmpty(newName)) return;
        _identity.DisplayName = newName;
        _identity.SaveToFile(_identityPath);
        BrainNameText.Text = newName;
        StatusText.Text = $"Brain name updated to '{newName}'";
    }

    private void ChangeVaultPath_Click(object s, RoutedEventArgs e)
    {
        MessageBox.Show(
            "To change vault path, restart ObsidianX with:\n\n" +
            "  ObsidianX.Client.exe \"C:\\path\\to\\vault\"\n\n" +
            $"Current: {_vaultPath}",
            "Change Vault Path", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveServerUrl_Click(object s, RoutedEventArgs e)
    {
        var url = SettingsServerUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;
        _serverUrl = url;
        SaveSettingsToFile();
        StatusText.Text = $"Server URL saved: {url}";
    }

    private string SettingsFilePath => Path.Combine(_vaultPath, ".obsidianx", "settings.json");

    private void SaveSettingsToFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var settings = new Dictionary<string, object>
            {
                ["ServerUrl"] = _serverUrl,
                ["BrainName"] = _identity.DisplayName,
                ["ScanPaths"] = _scanPaths,
                ["ScanWholeMachine"] = _scanWholeMachine,
                ["ScanPatterns"] = _scanPatterns,
                ["AutoScanOnStartup"] = _autoScanOnStartup,
                ["ImportMode"] = _importMode.ToString(),
                ["AutoLinkEnabled"] = _autoLinkEnabled,
                ["ShowAutoEdges"] = _showAutoEdges,
                ["AutoLinkThreshold"] = _autoLinkThreshold,
                ["StorageProvider"] = _storageProvider,
                ["MySqlConnectionString"] = _mySqlConnString,
                ["MaxVisibleNodes"] = _maxVisibleNodes,
                ["CullDistance"] = _cullDistance,
                ["UseClusterColors"] = _useClusterColors
            };
            File.WriteAllText(SettingsFilePath,
                Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented));
        }
        catch (Exception ex) { Debug.WriteLine($"Settings save error: {ex.Message}"); }
    }

    private void LoadSettingsFromFile()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var json = File.ReadAllText(SettingsFilePath);
            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            if (settings == null) return;
            if (settings.TryGetValue("ServerUrl", out var url) && url != null)
                _serverUrl = url.ToString() ?? _serverUrl;
            if (settings.TryGetValue("ScanWholeMachine", out var swm) && swm != null)
                bool.TryParse(swm.ToString(), out _scanWholeMachine);
            if (settings.TryGetValue("ScanPatterns", out var sp) && sp != null)
                _scanPatterns = sp.ToString() ?? _scanPatterns;
            if (settings.TryGetValue("AutoScanOnStartup", out var asos) && asos != null)
                bool.TryParse(asos.ToString(), out _autoScanOnStartup);
            if (settings.TryGetValue("ImportMode", out var im) && im != null)
                Enum.TryParse<VaultImporter.ImportMode>(im.ToString(), out _importMode);
            if (settings.TryGetValue("ScanPaths", out var paths) && paths is Newtonsoft.Json.Linq.JArray arr)
            {
                _scanPaths.Clear();
                foreach (var p in arr) if (p != null) _scanPaths.Add(p.ToString());
            }
            if (settings.TryGetValue("AutoLinkEnabled", out var ale) && ale != null)
                bool.TryParse(ale.ToString(), out _autoLinkEnabled);
            if (settings.TryGetValue("ShowAutoEdges", out var sae) && sae != null)
                bool.TryParse(sae.ToString(), out _showAutoEdges);
            if (settings.TryGetValue("AutoLinkThreshold", out var alt) && alt != null)
                double.TryParse(alt.ToString(), System.Globalization.CultureInfo.InvariantCulture, out _autoLinkThreshold);
            if (settings.TryGetValue("StorageProvider", out var sp2) && sp2 != null)
                _storageProvider = sp2.ToString() ?? _storageProvider;
            if (settings.TryGetValue("MySqlConnectionString", out var mcs) && mcs != null)
                _mySqlConnString = mcs.ToString() ?? _mySqlConnString;
            if (settings.TryGetValue("MaxVisibleNodes", out var mvn) && mvn != null)
                int.TryParse(mvn.ToString(), out _maxVisibleNodes);
            if (settings.TryGetValue("CullDistance", out var cd) && cd != null)
                double.TryParse(cd.ToString(), System.Globalization.CultureInfo.InvariantCulture, out _cullDistance);
            if (settings.TryGetValue("UseClusterColors", out var ucc) && ucc != null)
                bool.TryParse(ucc.ToString(), out _useClusterColors);
        }
        catch (Exception ex) { Debug.WriteLine($"Settings load error: {ex.Message}"); }
    }

    // ═══════════════════════════════════════
    // AUTO-IMPORT SCANNER + BRAIN EXPORT
    // ═══════════════════════════════════════

    private ImportOptions BuildImportOptions() => new()
    {
        VaultPath = _vaultPath,
        ScanPaths = [.._scanPaths],
        ScanWholeMachine = _scanWholeMachine,
        Patterns = _scanPatterns,
        Mode = _importMode
    };

    private void PopulateImportSettings()
    {
        if (ScanPathsList == null) return;
        ScanPathsList.Items.Clear();
        foreach (var p in _scanPaths) ScanPathsList.Items.Add(p);
        ScanPatternsBox.Text = _scanPatterns;
        ScanWholeMachineCheck.IsChecked = _scanWholeMachine;
        AutoScanOnStartupCheck.IsChecked = _autoScanOnStartup;
        ImportModeCombo.SelectedIndex = _importMode == VaultImporter.ImportMode.Copy ? 1 : 0;
    }

    private void AddScanPath_Click(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder to scan for CLAUDE.md / README.md / *.md"
        };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(path) || _scanPaths.Contains(path)) return;
        _scanPaths.Add(path);
        ScanPathsList.Items.Add(path);
        SaveSettingsToFile();
        StatusText.Text = $"Added scan path: {path}";
    }

    private void RemoveScanPath_Click(object s, RoutedEventArgs e)
    {
        if (ScanPathsList.SelectedItem is not string path) return;
        _scanPaths.Remove(path);
        ScanPathsList.Items.Remove(path);
        SaveSettingsToFile();
        StatusText.Text = $"Removed scan path: {path}";
    }

    private void ScanWholeMachineCheck_Changed(object s, RoutedEventArgs e)
    {
        _scanWholeMachine = ScanWholeMachineCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void AutoScanOnStartup_Changed(object s, RoutedEventArgs e)
    {
        _autoScanOnStartup = AutoScanOnStartupCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void SaveScanPatterns_Click(object s, RoutedEventArgs e)
    {
        _scanPatterns = string.IsNullOrWhiteSpace(ScanPatternsBox.Text)
            ? "CLAUDE.md;README.md;*.md" : ScanPatternsBox.Text.Trim();
        SaveSettingsToFile();
        StatusText.Text = $"Patterns saved: {_scanPatterns}";
    }

    private void ImportMode_Changed(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _importMode = ImportModeCombo.SelectedIndex == 1
            ? VaultImporter.ImportMode.Copy : VaultImporter.ImportMode.Reference;
        SaveSettingsToFile();
    }

    private async void PreviewScan_Click(object s, RoutedEventArgs e)
    {
        if (_scanPaths.Count == 0 && !_scanWholeMachine)
        {
            MessageBox.Show("Add at least one scan path, or enable \"Scan Whole Machine\".",
                "Resonance Scan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ScanStatusText.Text = "Scanning (Resonance Scan running)…";
        PreviewScanButton.IsEnabled = false;
        ImportScanButton.IsEnabled = false;

        var opts = BuildImportOptions();
        var report = await Task.Run(() => _importer.Scan(opts));
        _lastScanHits = report.Hits;

        ScanResultsList.Items.Clear();
        foreach (var hit in report.Hits.Take(500))
        {
            ScanResultsList.Items.Add(
                $"[{hit.ResonanceScore,5:F2}]  {hit.FileName,-18}  {hit.SizeBytes,8:N0} B  {hit.SourcePath}");
        }

        ScanStatusText.Text =
            $"Found {report.Hits.Count} files · visited {report.VisitedFolders} folders · " +
            $"pruned {report.PrunedFolders} · {report.ProjectRootsDetected} project roots · " +
            $"skipped {report.NearDuplicatesSkipped} near-dups, {report.ExactDuplicatesSkipped} exact dups";
        PreviewScanButton.IsEnabled = true;
        ImportScanButton.IsEnabled = report.Hits.Count > 0;
    }

    private async void ImportScan_Click(object s, RoutedEventArgs e)
    {
        if (_lastScanHits.Count == 0)
        {
            MessageBox.Show("Run a preview scan first.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(
            $"Import {_lastScanHits.Count} files into this vault as {_importMode}?",
            "Confirm import", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        ImportScanButton.IsEnabled = false;
        PreviewScanButton.IsEnabled = false;
        ScanStatusText.Text = "Importing…";

        var opts = BuildImportOptions();
        var result = await Task.Run(() => _importer.Import(_lastScanHits, opts));

        ScanStatusText.Text =
            $"Imported {result.Imported.Count} · skipped {result.Skipped.Count} · errors {result.Errors.Count}";

        // Re-index + refresh everything so graph shows the new notes
        IndexVault();
        _dashPhysics.LoadFromGraphDiff(_graph);
        _graphPhysics.LoadFromGraphDiff(_graph);
        UpdateUI();
        RefreshVaultTree();

        PreviewScanButton.IsEnabled = true;
        ImportScanButton.IsEnabled = true;
    }

    private void ExportBrain_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var result = _exporter.Export(_vaultPath, _identity, _graph);
            ExportStatusText.Text =
                $"Exported {result.NodeCount} nodes → brain-export.json / .md / manifest.json · CLAUDE.md updated";
            StatusText.Text = "Brain exported. External tools can now read .obsidianx/brain-export.json";
        }
        catch (Exception ex)
        {
            ExportStatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private void OpenExportFolder_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(_vaultPath, ".obsidianx");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = $"Open folder failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════
    // MCP — Claude Code CLI integration
    // ═══════════════════════════════════════

    private string McpServerExePath()
    {
        // Prefer the built artifact next to the solution
        var root = FindSolutionRoot();
        var candidate = Path.Combine(root, "ObsidianX.Mcp", "bin", "Release", "net9.0", "obsidianx-mcp.exe");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(root, "ObsidianX.Mcp", "bin", "Debug", "net9.0", "obsidianx-mcp.exe");
        return candidate;
    }

    private string FindSolutionRoot()
    {
        // Walk up from AppContext.BaseDirectory looking for ObsidianX.slnx
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ObsidianX.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private void PopulateMcpCommands()
    {
        var exe = McpServerExePath();
        var built = File.Exists(exe);
        var cmd = $"claude mcp add obsidianx-brain -s user -e OBSIDIANX_VAULT=\"{_vaultPath}\" -- \"{exe}\"";
        McpInstallCommand.Text = cmd;

        var config = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["obsidianx-brain"] = new
                {
                    command = exe,
                    args = Array.Empty<string>(),
                    env = new Dictionary<string, string> { ["OBSIDIANX_VAULT"] = _vaultPath }
                }
            }
        }, Newtonsoft.Json.Formatting.Indented);
        McpManualConfig.Text = config;

        McpStatusText.Text = built
            ? $"MCP server ready · {exe}"
            : $"MCP server not built yet — click 'Build MCP Server' first. Expected at:\n{exe}";
    }

    private void BuildMcpServer_Click(object s, RoutedEventArgs e)
    {
        McpStatusText.Text = "Building MCP server (Release)…";
        BuildMcpAsync();
    }

    private async void BuildMcpAsync()
    {
        try
        {
            var root = FindSolutionRoot();
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("ObsidianX.Mcp/ObsidianX.Mcp.csproj");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Release");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("quiet");

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                PopulateMcpCommands();
                McpStatusText.Text = $"Built successfully · {McpServerExePath()}";
            }
            else
            {
                McpStatusText.Text = $"Build failed (exit {proc.ExitCode}):\n{stderr}\n{stdout}";
            }
        }
        catch (Exception ex)
        {
            McpStatusText.Text = $"Build error: {ex.Message}";
        }
    }

    /// <summary>
    /// Resolves the `claude` CLI on Windows. npm global installs drop a
    /// `claude.cmd` shim in %APPDATA%\npm; UseShellExecute=false won't
    /// locate .cmd files via PATH without an explicit extension, so we
    /// search the usual places ourselves.
    /// </summary>
    private static string? FindClaudeCli()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new List<string> { Path.Combine(roaming, "npm") };
        dirs.AddRange(pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries));

        foreach (var name in new[] { "claude.cmd", "claude.exe", "claude.bat", "claude" })
        {
            foreach (var d in dirs)
            {
                try
                {
                    var p = Path.Combine(d.Trim(), name);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { /* skip invalid path entry */ }
            }
        }
        return null;
    }

    private static async Task<(int code, string stdout, string stderr)> RunClaudeCliAsync(params string[] args)
    {
        var cli = FindClaudeCli();
        if (cli == null) throw new FileNotFoundException("claude CLI not found on PATH or in %APPDATA%\\npm");

        ProcessStartInfo psi;
        if (cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
         || cli.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // .cmd / .bat must run through cmd.exe
            psi = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(cli);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(cli)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    private async void InstallMcp_Click(object s, RoutedEventArgs e)
    {
        var exe = McpServerExePath();
        if (!File.Exists(exe))
        {
            McpStatusText.Text = "❌ MCP server not built yet. Click 'Build MCP Server' first.";
            return;
        }

        var summary = new System.Text.StringBuilder();

        // ── Claude Code CLI (terminal) ──
        var cli = FindClaudeCli();
        if (cli == null)
        {
            summary.AppendLine("⚠ Claude Code CLI not detected (skipping).");
            summary.AppendLine("   Install via: npm i -g @anthropic-ai/claude-code");
        }
        else
        {
            try
            {
                McpStatusText.Text = "⏳ Installing to Claude Code CLI (user scope)…";
                await RunClaudeCliAsync("mcp", "remove", "obsidianx-brain", "-s", "local");
                await RunClaudeCliAsync("mcp", "remove", "obsidianx-brain", "-s", "user");

                var (code, _, stderr) = await RunClaudeCliAsync(
                    "mcp", "add", "obsidianx-brain",
                    "-s", "user",
                    "-e", $"OBSIDIANX_VAULT={_vaultPath}",
                    "--", exe);

                var (_, listOut, _) = await RunClaudeCliAsync("mcp", "list");
                if (listOut.Contains("obsidianx-brain") && listOut.Contains("Connected"))
                    summary.AppendLine("✅ Claude Code CLI — connected (user scope)");
                else if (code == 0)
                    summary.AppendLine("⚠ Claude Code CLI — registered but can't verify");
                else
                    summary.AppendLine($"❌ Claude Code CLI — failed: {stderr}");
            }
            catch (Exception ex)
            {
                summary.AppendLine($"❌ Claude Code CLI — error: {ex.Message}");
            }
        }

        // ── Claude Desktop ──
        try
        {
            var desktopResult = InstallToClaudeDesktop(exe);
            summary.AppendLine(desktopResult);
        }
        catch (Exception ex)
        {
            summary.AppendLine($"❌ Claude Desktop — error: {ex.Message}");
        }

        McpStatusText.Text = summary.ToString();
    }

    /// <summary>
    /// Merge the obsidianx-brain server entry into Claude Desktop's
    /// claude_desktop_config.json at %APPDATA%\Claude. Preserves any
    /// other mcpServers the user has configured. Creates the file /
    /// directory if Claude Desktop is installed but has never been
    /// configured before. Claude Desktop must be restarted to pick up
    /// the change — config is read at app launch.
    /// </summary>
    private string InstallToClaudeDesktop(string exe)
    {
        var cfgPath = ClaudeDesktopConfigPath();
        var cfgDir = Path.GetDirectoryName(cfgPath)!;

        // If the Claude folder doesn't exist, Claude Desktop probably isn't
        // installed. We still write it — harmless if the app is installed
        // later — but flag it in the status.
        bool wasInstalled = Directory.Exists(cfgDir);

        Newtonsoft.Json.Linq.JObject config;
        if (File.Exists(cfgPath))
        {
            try { config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(cfgPath)); }
            catch { config = new Newtonsoft.Json.Linq.JObject(); }
        }
        else config = new Newtonsoft.Json.Linq.JObject();

        var servers = config["mcpServers"] as Newtonsoft.Json.Linq.JObject;
        if (servers == null)
        {
            servers = new Newtonsoft.Json.Linq.JObject();
            config["mcpServers"] = servers;
        }

        servers["obsidianx-brain"] = new Newtonsoft.Json.Linq.JObject
        {
            ["command"] = exe,
            ["args"] = new Newtonsoft.Json.Linq.JArray(),
            ["env"] = new Newtonsoft.Json.Linq.JObject
            {
                ["OBSIDIANX_VAULT"] = _vaultPath
            }
        };

        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(cfgPath,
            config.ToString(Newtonsoft.Json.Formatting.Indented));

        return wasInstalled
            ? $"✅ Claude Desktop — merged into {cfgPath}\n   (restart Claude Desktop to activate)"
            : $"⚠ Claude Desktop — config written at {cfgPath}\n   (Claude Desktop not detected — install & restart it to use)";
    }

    private string UninstallFromClaudeDesktop()
    {
        var cfgPath = ClaudeDesktopConfigPath();
        if (!File.Exists(cfgPath)) return "(Claude Desktop config not found)";

        try
        {
            var config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(cfgPath));
            var servers = config["mcpServers"] as Newtonsoft.Json.Linq.JObject;
            if (servers != null && servers.Remove("obsidianx-brain"))
            {
                File.WriteAllText(cfgPath,
                    config.ToString(Newtonsoft.Json.Formatting.Indented));
                return "Removed from Claude Desktop config";
            }
            return "(obsidianx-brain not registered in Claude Desktop)";
        }
        catch (Exception ex)
        {
            return $"Claude Desktop uninstall failed: {ex.Message}";
        }
    }

    private static string ClaudeDesktopConfigPath()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appdata, "Claude", "claude_desktop_config.json");
    }

    // ═══════════════════════════════════════
    // MCP CONNECTION STATUS BAR INDICATORS
    // ═══════════════════════════════════════

    private DispatcherTimer? _mcpStatusTimer;
    private DateTime _lastMcpActivity = DateTime.MinValue;

    private void StartMcpStatusWatcher()
    {
        // Read Claude config files directly — no need to spawn `claude` every tick.
        _mcpStatusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _mcpStatusTimer.Tick += (_, _) =>
        {
            RefreshMcpStatusBar();
            RefreshAiBackendStatusBar();
        };
        _mcpStatusTimer.Start();
        RefreshMcpStatusBar();
        RefreshAiBackendStatusBar();
    }

    /// <summary>Poll the AI Hub and reflect current backend + loaded
    /// model in the status bar every 3s together with the MCP LEDs.</summary>
    private async void RefreshAiBackendStatusBar()
    {
        if (AiBackendDot == null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var json = await http.GetStringAsync(AiServerBase + "/api/ai/models");
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            var running = root["running"] as Newtonsoft.Json.Linq.JArray ?? [];
            var installed = root["installed"] as Newtonsoft.Json.Linq.JArray ?? [];

            if (running.Count > 0)
            {
                var name = running[0]["Name"]?.ToString() ?? running[0]["name"]?.ToString() ?? "?";
                AiBackendDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
                AiBackendStatus.Text = $"AI: {name} (warm)";
            }
            else if (installed.Count > 0)
            {
                AiBackendDot.Fill = (SolidColorBrush)FindResource("NeonCyanBrush");
                AiBackendStatus.Text = $"AI: {installed.Count} models · idle";
            }
            else
            {
                AiBackendDot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
                AiBackendStatus.Text = "AI: no models";
            }
        }
        catch
        {
            AiBackendDot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
            AiBackendStatus.Text = "AI: offline";
        }
    }

    private void RefreshMcpStatusBar()
    {
        if (McpCliDot == null) return;

        // Claude Code CLI — check user-scope entry in ~/.claude.json
        var cliOk = IsRegisteredInClaudeCli();
        McpCliDot.Fill = cliOk
            ? (SolidColorBrush)FindResource("NeonGreenBrush")
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
        McpCliStatus.Text = cliOk ? "CLI: ✓ live" : "CLI: off";

        // Claude Desktop — check claude_desktop_config.json
        var desktopOk = IsRegisteredInClaudeDesktop();
        McpDesktopDot.Fill = desktopOk
            ? (SolidColorBrush)FindResource("NeonGreenBrush")
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
        McpDesktopStatus.Text = desktopOk ? "Desktop: ✓ live" : "Desktop: off";

        // Recent activity — seconds since last access-log event
        var sinceLast = _lastMcpActivity == DateTime.MinValue
            ? double.MaxValue
            : (DateTime.UtcNow - _lastMcpActivity).TotalSeconds;

        if (sinceLast < 3)
        {
            McpActivityDot.Fill = (SolidColorBrush)FindResource("NeonCyanBrush");
            McpActivityStatus.Text = "ACTIVE";
            McpActivityStatus.Foreground = (SolidColorBrush)FindResource("NeonCyanBrush");
        }
        else if (sinceLast < 30)
        {
            McpActivityDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
            McpActivityStatus.Text = $"{(int)sinceLast}s ago";
            McpActivityStatus.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        }
        else
        {
            McpActivityDot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
            McpActivityStatus.Text = "idle";
            McpActivityStatus.Foreground = (SolidColorBrush)FindResource("TextMutedBrush");
        }
    }

    private static bool IsRegisteredInClaudeCli()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".claude.json");
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            // Quick contains — way cheaper than full JSON parse every 3s
            return json.Contains("\"obsidianx-brain\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsRegisteredInClaudeDesktop()
    {
        try
        {
            var cfg = ClaudeDesktopConfigPath();
            if (!File.Exists(cfg)) return false;
            return File.ReadAllText(cfg)
                .Contains("\"obsidianx-brain\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async void TestMcp_Click(object s, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        var exe = McpServerExePath();
        sb.AppendLine($"▸ MCP exe: {exe}  {(File.Exists(exe) ? "(exists)" : "(MISSING — build it)")}");
        sb.AppendLine($"▸ Vault:   {_vaultPath}");
        sb.AppendLine();

        // Claude Code CLI
        try
        {
            var cli = FindClaudeCli();
            if (cli == null)
            {
                sb.AppendLine("▸ Claude Code CLI: ❌ not found (install: npm i -g @anthropic-ai/claude-code)");
            }
            else
            {
                var (_, stdout, _) = await RunClaudeCliAsync("mcp", "list");
                if (stdout.Contains("obsidianx-brain") && stdout.Contains("Connected"))
                    sb.AppendLine($"▸ Claude Code CLI: ✅ registered AND connected ({cli})");
                else if (stdout.Contains("obsidianx-brain"))
                    sb.AppendLine("▸ Claude Code CLI: ⚠ registered but not connected");
                else
                    sb.AppendLine("▸ Claude Code CLI: ❌ NOT registered");
            }
        }
        catch (Exception ex) { sb.AppendLine($"▸ Claude Code CLI: ❌ error: {ex.Message}"); }

        // Claude Desktop
        try
        {
            var cfgPath = ClaudeDesktopConfigPath();
            if (!File.Exists(cfgPath))
                sb.AppendLine($"▸ Claude Desktop:  ❌ config not found at {cfgPath}");
            else
            {
                var cfg = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(cfgPath));
                var servers = cfg["mcpServers"] as Newtonsoft.Json.Linq.JObject;
                if (servers?["obsidianx-brain"] != null)
                    sb.AppendLine($"▸ Claude Desktop:  ✅ registered in {cfgPath}");
                else
                    sb.AppendLine($"▸ Claude Desktop:  ❌ NOT registered (config exists but no obsidianx-brain)");
            }
        }
        catch (Exception ex) { sb.AppendLine($"▸ Claude Desktop: ❌ error: {ex.Message}"); }

        McpStatusText.Text = sb.ToString();
    }

    private async void UninstallMcp_Click(object s, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            await RunClaudeCliAsync("mcp", "remove", "obsidianx-brain", "-s", "local");
            await RunClaudeCliAsync("mcp", "remove", "obsidianx-brain", "-s", "user");
            sb.AppendLine("✓ Removed from Claude Code CLI (both scopes)");
        }
        catch (Exception ex) { sb.AppendLine($"✗ Claude Code CLI uninstall: {ex.Message}"); }

        sb.AppendLine("✓ " + UninstallFromClaudeDesktop());

        McpStatusText.Text = sb.ToString();
    }

    private void CopyMcpCommand_Click(object s, RoutedEventArgs e)
    {
        try { Clipboard.SetText(McpInstallCommand.Text); McpStatusText.Text = "Command copied to clipboard."; }
        catch (Exception ex) { McpStatusText.Text = $"Copy failed: {ex.Message}"; }
    }

    private void CopyMcpConfig_Click(object s, RoutedEventArgs e)
    {
        try { Clipboard.SetText(McpManualConfig.Text); McpStatusText.Text = "JSON config copied to clipboard."; }
        catch (Exception ex) { McpStatusText.Text = $"Copy failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════
    // AUTO-LINKER UI HANDLERS
    // ═══════════════════════════════════════

    private void AutoLinkEnabled_Changed(object s, RoutedEventArgs e)
    {
        _autoLinkEnabled = AutoLinkEnabledCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void ShowAutoEdges_Changed(object s, RoutedEventArgs e)
    {
        _showAutoEdges = ShowAutoEdgesCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void AutoLinkThreshold_Changed(object s,
        RoutedPropertyChangedEventArgs<double> e)
    {
        _autoLinkThreshold = e.NewValue;
        if (AutoLinkThresholdText != null)
            AutoLinkThresholdText.Text = e.NewValue.ToString("F2");
        SaveSettingsToFile();
    }

    private void PopulateAutoLinkerSettings()
    {
        if (AutoLinkEnabledCheck == null) return;
        AutoLinkEnabledCheck.IsChecked = _autoLinkEnabled;
        ShowAutoEdgesCheck.IsChecked = _showAutoEdges;
        AutoLinkThresholdSlider.Value = _autoLinkThreshold;
        AutoLinkThresholdText.Text = _autoLinkThreshold.ToString("F2");
    }

    // ═══════════════════════════════════════
    // STORAGE PROVIDER (SQLite / MySQL) UI
    // ═══════════════════════════════════════

    private void PopulateStorageSettings()
    {
        if (StorageProviderCombo == null) return;
        StorageProviderCombo.SelectedIndex = _storageProvider.Equals("MySql", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        MySqlConnStringBox.Text = _mySqlConnString;
        UpdateStorageStatus();
    }

    private void StorageProvider_Changed(object s, SelectionChangedEventArgs e)
    {
        // XAML fires SelectionChanged before named children are hooked up —
        // guard every field we touch.
        if (StorageProviderCombo == null || MySqlPanel == null) return;
        _storageProvider = StorageProviderCombo.SelectedIndex == 1 ? "MySql" : "Sqlite";
        MySqlPanel.Visibility = _storageProvider == "MySql" ? Visibility.Visible : Visibility.Collapsed;
        SaveSettingsToFile();
    }

    private void SaveMySqlConn_Click(object s, RoutedEventArgs e)
    {
        _mySqlConnString = MySqlConnStringBox.Text.Trim();
        SaveSettingsToFile();
        ApplyStorage();
    }

    private void ApplyStorage_Click(object s, RoutedEventArgs e) => ApplyStorage();

    private void ApplyStorage()
    {
        try
        {
            _storage?.Dispose();
            _storage = BrainStorageFactory.Create(_storageProvider, _vaultPath, _mySqlConnString);
            _storage.UpsertGraph(_graph);
            UpdateStorageStatus();
            StatusText.Text = $"Storage switched to {_storage.ProviderName} · {_storage.NodeCount()} nodes persisted";
        }
        catch (Exception ex)
        {
            StorageStatusText.Text = $"Failed to open storage: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════
    // GRAPH PERFORMANCE HANDLERS
    // ═══════════════════════════════════════

    private void MaxVisibleNodes_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        _maxVisibleNodes = (int)e.NewValue;
        if (MaxVisibleNodesText != null)
            MaxVisibleNodesText.Text = _maxVisibleNodes == 0 ? "∞" : _maxVisibleNodes.ToString();
        SaveSettingsToFile();
    }

    private void CullDistance_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        _cullDistance = e.NewValue;
        if (CullDistanceText != null)
            CullDistanceText.Text = _cullDistance == 0 ? "off" : _cullDistance.ToString("F0");
        SaveSettingsToFile();
    }

    private void ClusterColors_Changed(object s, RoutedEventArgs e)
    {
        _useClusterColors = ClusterColorsCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void PopulateGraphPerfSettings()
    {
        if (MaxVisibleNodesSlider == null) return;
        MaxVisibleNodesSlider.Value = _maxVisibleNodes;
        MaxVisibleNodesText.Text = _maxVisibleNodes == 0 ? "∞" : _maxVisibleNodes.ToString();
        CullDistanceSlider.Value = _cullDistance;
        CullDistanceText.Text = _cullDistance == 0 ? "off" : _cullDistance.ToString("F0");
        ClusterColorsCheck.IsChecked = _useClusterColors;
    }

    // ═══════════════════════════════════════
    // CUSTOM CATEGORY UI
    // ═══════════════════════════════════════

    private string? _editingCategoryId;   // null = creating new

    private void PopulateCustomCategories()
    {
        if (CustomCategoryList == null) return;
        _categories ??= new CategoryRegistry(_vaultPath);

        CustomCategoryList.Items.Clear();
        foreach (var c in _categories.All)
        {
            var label = string.IsNullOrEmpty(c.ColorHex) ? "" : $"  [{c.ColorHex}]";
            CustomCategoryList.Items.Add(
                $"{c.DisplayName}{label}   ·  {c.KeywordsEn.Count} EN / {c.KeywordsTh.Count} TH keywords");
        }
        if (CategoryStatusText != null)
            CategoryStatusText.Text = $"{_categories.All.Count} custom category·ies defined";
    }

    private void NewCategory_Click(object s, RoutedEventArgs e)
    {
        _editingCategoryId = null;
        CategoryNameBox.Text = "";
        CategoryColorBox.Text = "#FF00F0FF";
        CategoryEnBox.Text = "";
        CategoryThBox.Text = "";
        CategoryDescBox.Text = "";
        CategoryEditor.Visibility = Visibility.Visible;
    }

    private void EditCategory_Click(object s, RoutedEventArgs e)
    {
        if (CustomCategoryList.SelectedIndex < 0 || _categories == null) return;
        var cat = _categories.All[CustomCategoryList.SelectedIndex];
        _editingCategoryId = cat.Id;
        CategoryNameBox.Text = cat.DisplayName;
        CategoryColorBox.Text = cat.ColorHex;
        CategoryEnBox.Text = string.Join(", ", cat.KeywordsEn);
        CategoryThBox.Text = string.Join(", ", cat.KeywordsTh);
        CategoryDescBox.Text = cat.Description;
        CategoryEditor.Visibility = Visibility.Visible;
    }

    private void DeleteCategory_Click(object s, RoutedEventArgs e)
    {
        if (CustomCategoryList.SelectedIndex < 0 || _categories == null) return;
        var cat = _categories.All[CustomCategoryList.SelectedIndex];
        var confirm = MessageBox.Show(
            $"Delete custom category \"{cat.DisplayName}\"?\n\nNotes assigned to it will fall back to their built-in category on next index.",
            "Delete category", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        _categories.Remove(cat.Id);
        PopulateCustomCategories();
        ReindexAfterCategoryChange();
    }

    private void SaveCategory_Click(object s, RoutedEventArgs e)
    {
        _categories ??= new CategoryRegistry(_vaultPath);
        var name = CategoryNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            CategoryStatusText.Text = "Name is required.";
            return;
        }

        var en = CategoryEnBox.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries
                                                                | StringSplitOptions.TrimEntries).ToList();
        var th = CategoryThBox.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries
                                                                | StringSplitOptions.TrimEntries).ToList();

        if (_editingCategoryId == null)
        {
            var cat = new CustomCategory
            {
                DisplayName = name,
                ColorHex = CategoryColorBox.Text.Trim(),
                KeywordsEn = en,
                KeywordsTh = th,
                Description = CategoryDescBox.Text.Trim()
            };
            _categories.Add(cat);
        }
        else
        {
            var existing = _categories.FindById(_editingCategoryId);
            if (existing == null)
            {
                CategoryStatusText.Text = "Category vanished — reloading.";
                PopulateCustomCategories();
                return;
            }
            existing.DisplayName = name;
            existing.ColorHex = CategoryColorBox.Text.Trim();
            existing.KeywordsEn = en;
            existing.KeywordsTh = th;
            existing.Description = CategoryDescBox.Text.Trim();
            _categories.Update(existing);
        }

        CategoryEditor.Visibility = Visibility.Collapsed;
        _editingCategoryId = null;
        PopulateCustomCategories();
        ReindexAfterCategoryChange();
    }

    private void CancelCategoryEdit_Click(object s, RoutedEventArgs e)
    {
        CategoryEditor.Visibility = Visibility.Collapsed;
        _editingCategoryId = null;
    }

    private void ReindexAfterCategoryChange()
    {
        IndexVault();
        _dashPhysics.LoadFromGraphDiff(_graph);
        _graphPhysics.LoadFromGraphDiff(_graph);
        UpdateUI();
        RefreshVaultTree();
        CategoryStatusText.Text = $"Re-indexed · {_graph.Nodes.Count(n => !string.IsNullOrEmpty(n.CustomCategoryId))} notes matched a custom category";
    }

    private void UpdateStorageStatus()
    {
        if (StorageStatusText == null) return;
        try
        {
            if (_storage == null)
                StorageStatusText.Text = "No storage initialized yet — it will auto-init on next index.";
            else
                StorageStatusText.Text = $"Active: {_storage.ProviderName} · {_storage.NodeCount()} nodes indexed";
        }
        catch (Exception ex)
        {
            StorageStatusText.Text = $"Status error: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════
    // ACCESS-LOG TAIL + PULSE INJECTION
    // Tails .obsidianx/access-log.ndjson (written by MCP server when
    // Claude pulls knowledge). For each new entry, we bump AccessIntensity
    // on the matching physics node. The render loop then draws those nodes
    // with a brighter emissive + larger radius that decays exponentially.
    // ═══════════════════════════════════════

    private string AccessLogPath => Path.Combine(_vaultPath, ".obsidianx", "access-log.ndjson");

    private void StartAccessLogWatcher()
    {
        // Seek past existing content on startup so we only react to NEW hits
        try
        {
            if (File.Exists(AccessLogPath))
                _accessLogOffset = new FileInfo(AccessLogPath).Length;
        }
        catch { _accessLogOffset = 0; }

        _accessLogTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _accessLogTimer.Tick += (_, _) => PollAccessLog();
        _accessLogTimer.Start();
    }

    private void PollAccessLog()
    {
        try
        {
            if (!File.Exists(AccessLogPath)) return;
            var fi = new FileInfo(AccessLogPath);
            // File was truncated (e.g. trim in MCP) — reset offset
            if (fi.Length < _accessLogOffset) _accessLogOffset = 0;
            if (fi.Length == _accessLogOffset) return;

            using var fs = new FileStream(AccessLogPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_accessLogOffset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);

            string? line;
            int newEvents = 0;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                HandleAccessLine(line);
                newEvents++;
            }
            _accessLogOffset = fs.Position;

            if (newEvents > 0)
            {
                _recentAccessCount += newEvents;
                _lastMcpActivity = DateTime.UtcNow;    // status-bar activity LED
                StatusText.Text = $"🧠 Claude pulled knowledge · {newEvents} node(s) pulsed · total session: {_recentAccessCount}";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void HandleAccessLine(string json)
    {
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var nodeId = obj["node_id"]?.ToString();
            if (string.IsNullOrEmpty(nodeId)) return;

            var op = obj["op"]?.ToString() ?? "mcp";
            var bumped = BumpPulseForNode(_dashPhysics, nodeId, op)
                       | BumpPulseForNode(_graphPhysics, nodeId, op);

            // Fallback: if the access-log carries a stale id (e.g. the
            // brain was re-exported with different ids since the log was
            // written), try matching via the context field which carries
            // a title or path hint.
            if (!bumped)
            {
                var hint = obj["context"]?.ToString();
                if (!string.IsNullOrEmpty(hint))
                {
                    FindAndBumpByTitleOrPath(_dashPhysics, hint, op);
                    FindAndBumpByTitleOrPath(_graphPhysics, hint, op);
                }
            }

            // Also persist into storage for "top accessed" queries
            try
            {
                _storage?.LogAccess(nodeId,
                    obj["op"]?.ToString() ?? "mcp",
                    obj["context"]?.ToString());
            }
            catch { /* storage is best-effort */ }
        }
        catch (Newtonsoft.Json.JsonException) { }
    }

    private static void FindAndBumpByTitleOrPath(PhysicsEngine physics, string hint, string op = "mcp")
    {
        foreach (var n in physics.Nodes)
        {
            if (n.Title.Equals(hint, StringComparison.OrdinalIgnoreCase))
            {
                n.AccessIntensity = Math.Min(1.0, n.AccessIntensity + 0.8);
                n.AccessCount++;
                n.LastAccessedAt = DateTime.UtcNow;
                n.LastOp = op;
                return;
            }
        }
    }

    /// <summary>
    /// Bump AccessIntensity on the node whose KnowledgeGraph file path
    /// matches. Used for write events (editor save) and selection events
    /// so Real Brain camera follows user activity in addition to MCP.
    /// </summary>
    private void BumpNodeActivityByPath(string filePath, string op)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        var gnode = _graph.Nodes.FirstOrDefault(
            n => n.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        if (gnode == null) return;

        BumpPulseForNode(_dashPhysics, gnode.Id);
        BumpPulseForNode(_graphPhysics, gnode.Id);
        try { _storage?.LogAccess(gnode.Id, op, Path.GetFileName(filePath)); }
        catch { /* best-effort */ }
    }

    private static bool BumpPulseForNode(PhysicsEngine physics, string nodeId, string op = "mcp")
    {
        var node = physics.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return false;
        node.AccessIntensity = Math.Min(1.0, node.AccessIntensity + 0.8);
        node.AccessCount++;
        node.LastAccessedAt = DateTime.UtcNow;
        node.LastOp = op;
        return true;
    }

    private static void DecayPulses(PhysicsEngine physics, double dt)
    {
        // Exponential decay with ~2.5s half-life
        var factor = Math.Pow(0.5, dt / 2.5);
        foreach (var n in physics.Nodes)
        {
            if (n.AccessIntensity > 0.001)
                n.AccessIntensity *= factor;
            else
                n.AccessIntensity = 0;
        }
    }

    // ═══════════════════════════════════════
    // EDITOR — Toolbar Actions
    // ═══════════════════════════════════════

    private void EditorNew_Click(object s, RoutedEventArgs e) => CreateNewNote();
    private void EditorH1_Click(object s, RoutedEventArgs e) => _mdEditor.InsertHeading(1);
    private void EditorH2_Click(object s, RoutedEventArgs e) => _mdEditor.InsertHeading(2);
    private void EditorBold_Click(object s, RoutedEventArgs e) => _mdEditor.ToggleBold();
    private void EditorItalic_Click(object s, RoutedEventArgs e) => _mdEditor.ToggleItalic();
    private void EditorLink_Click(object s, RoutedEventArgs e) => _mdEditor.InsertWikiLink();
    private void EditorCode_Click(object s, RoutedEventArgs e) => _mdEditor.InsertCodeBlock();
    private void EditorTask_Click(object s, RoutedEventArgs e) => _mdEditor.InsertTaskList();

    private void OpenFileInEditor(string filePath)
    {
        _mdEditor.OpenFile(filePath);
        EditorFileTitle.Text = Path.GetFileNameWithoutExtension(filePath);
        StatusText.Text = $"Editing: {Path.GetFileName(filePath)}";
        RefreshBacklinks();

        // Switch to editor view
        foreach (var kv in _viewMap)
        {
            var v = (UIElement?)FindName(kv.Value);
            if (v != null) v.Visibility = Visibility.Collapsed;
        }
        EditorView.Visibility = Visibility.Visible;
        Button[] navButtons = [NavDashboard, NavBrainGraph, NavNetwork, NavEditor, NavVault, NavSearch, NavClaude, NavGrowth, NavPeers, NavSharing, NavSettings];
        foreach (var nb in navButtons) nb.Style = (Style)FindResource("NavButton");
        NavEditor.Style = (Style)FindResource("NavButtonActive");
    }

    private void OnWikiLinkClicked(string linkName)
    {
        var resolved = _mdEditor.ResolveWikiLink(linkName);
        if (resolved != null)
            OpenFileInEditor(resolved);
        else
        {
            // Create new note for broken link
            var result = MessageBox.Show(
                $"Note \"{linkName}\" not found.\n\nCreate it?",
                "Create Note", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                var newPath = Path.Combine(_vaultPath, linkName + ".md");
                _mdEditor.NewFile(newPath);
                EditorFileTitle.Text = linkName;
                RefreshVaultTree();
            }
        }
    }

    private void RefreshBacklinks()
    {
        BacklinksPanel.Children.Clear();
        var backlinks = _mdEditor.GetBacklinks();
        if (backlinks.Count == 0)
        {
            BacklinksPanel.Children.Add(new TextBlock
            {
                Text = "No backlinks found",
                FontSize = 11, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontStyle = FontStyles.Italic
            });
            return;
        }

        foreach (var (filePath, title, context) in backlinks)
        {
            var btn = new Button
            {
                Style = (Style)FindResource("NavButton"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 2),
                Tag = filePath
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = $"\U0001F517 {title}",
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("NeonCyanBrush")
            });
            sp.Children.Add(new TextBlock
            {
                Text = context,
                FontSize = 9, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            btn.Content = sp;
            btn.Click += (_, _) => OpenFileInEditor(filePath);
            BacklinksPanel.Children.Add(btn);
        }
    }

    private void CreateNewNote()
    {
        var dialog = new Window
        {
            Title = "New Note",
            Width = 400, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0))
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "Note Title:", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox
        {
            FontSize = 14, Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A))
        };
        sp.Children.Add(tb);
        var okBtn = new Button
        {
            Content = "Create", Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(20, 8, 20, 8), HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(0, 240, 255)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x1A)),
            FontWeight = FontWeights.Bold, Cursor = Cursors.Hand
        };
        okBtn.Click += (_, _) =>
        {
            var name = tb.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            dialog.DialogResult = true;
            dialog.Close();
        };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        sp.Children.Add(okBtn);
        dialog.Content = sp;

        if (dialog.ShowDialog() == true)
        {
            var name = tb.Text.Trim();
            var filePath = Path.Combine(_vaultPath, name + ".md");
            _mdEditor.NewFile(filePath);
            EditorFileTitle.Text = name;
            RefreshVaultTree();
            OpenFileInEditor(filePath);
        }
    }

    // ═══════════════════════════════════════
    // SEARCH
    // ═══════════════════════════════════════

    private void SearchBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ExecuteSearch();
    }

    private void SearchExecute_Click(object s, RoutedEventArgs e) => ExecuteSearch();

    private void ExecuteSearch()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        SearchResults.Children.Clear();
        var results = new List<(string FilePath, string Title, string Match, int MatchCount)>();

        foreach (var file in Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var matches = Regex.Matches(content, Regex.Escape(query), RegexOptions.IgnoreCase);
                if (matches.Count > 0)
                {
                    // Get first match with context
                    var m = matches[0];
                    int start = Math.Max(0, m.Index - 60);
                    int end = Math.Min(content.Length, m.Index + m.Length + 60);
                    var context = content[start..end].Replace("\n", " ").Trim();
                    var title = Path.GetFileNameWithoutExtension(file);
                    results.Add((file, title, context, matches.Count));
                }
            }
            catch (IOException) { /* Skip files that can't be read */ }
        }

        if (results.Count == 0)
        {
            SearchResults.Children.Add(new TextBlock
            {
                Text = $"No results found for \"{query}\"",
                FontSize = 13, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 20, 0, 0), FontStyle = FontStyles.Italic
            });
            return;
        }

        // Header
        SearchResults.Children.Add(new TextBlock
        {
            Text = $"{results.Count} note{(results.Count > 1 ? "s" : "")} found",
            FontSize = 12, Foreground = (SolidColorBrush)FindResource("NeonGreenBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        foreach (var (filePath, title, match, count) in results.OrderByDescending(r => r.MatchCount))
        {
            var card = new Border
            {
                Style = (Style)FindResource("CardPanel"),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand
            };
            var sp = new StackPanel();
            var header = new Grid();
            header.Children.Add(new TextBlock
            {
                Text = $"\U0001F4C4 {title}",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("NeonCyanBrush")
            });
            header.Children.Add(new TextBlock
            {
                Text = $"{count} match{(count > 1 ? "es" : "")}",
                FontSize = 10, Foreground = (SolidColorBrush)FindResource("NeonPinkBrush"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(header);

            // Highlight match in context
            var ctx = new TextBlock
            {
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            };
            var parts = Regex.Split(match, $"({Regex.Escape(query)})", RegexOptions.IgnoreCase);
            foreach (var part in parts)
            {
                if (part.Equals(query, StringComparison.OrdinalIgnoreCase))
                    ctx.Inlines.Add(new System.Windows.Documents.Run(part)
                    {
                        Foreground = new SolidColorBrush(Color.FromRgb(255, 214, 0)),
                        FontWeight = FontWeights.Bold,
                        Background = new SolidColorBrush(Color.FromArgb(40, 255, 214, 0))
                    });
                else
                    ctx.Inlines.Add(new System.Windows.Documents.Run(part));
            }
            sp.Children.Add(ctx);

            // Relative path
            sp.Children.Add(new TextBlock
            {
                Text = Path.GetRelativePath(_vaultPath, filePath),
                FontSize = 9, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 4, 0, 0), FontFamily = (FontFamily)FindResource("MonoFont")
            });

            card.Child = sp;
            card.MouseLeftButtonDown += (_, _) => OpenFileInEditor(filePath);
            SearchResults.Children.Add(card);
        }
    }

    // ═══════════════════════════════════════
    // QUICK SWITCHER (Ctrl+O)
    // ═══════════════════════════════════════

    private void OpenQuickSwitcher()
    {
        QuickSwitcherOverlay.Visibility = Visibility.Visible;
        QuickSwitcherInput.Text = "";
        QuickSwitcherInput.Focus();
        PopulateQuickSwitcher("");
    }

    private void QuickSwitcher_Close(object s, MouseButtonEventArgs e)
    {
        QuickSwitcherOverlay.Visibility = Visibility.Collapsed;
    }

    private void QuickSwitcher_TextChanged(object s, TextChangedEventArgs e)
    {
        PopulateQuickSwitcher(QuickSwitcherInput.Text.Trim());
    }

    private void QuickSwitcher_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            QuickSwitcherOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        if (e.Key == Key.Enter)
        {
            // Open first result
            if (QuickSwitcherResults.Children.Count > 0 && QuickSwitcherResults.Children[0] is Button btn && btn.Tag is string path)
            {
                QuickSwitcherOverlay.Visibility = Visibility.Collapsed;
                OpenFileInEditor(path);
            }
        }
    }

    private void PopulateQuickSwitcher(string filter)
    {
        QuickSwitcherResults.Children.Clear();
        if (!Directory.Exists(_vaultPath)) return;

        var files = Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\."))
            .Select(f => (Path: f, Name: Path.GetFileNameWithoutExtension(f)))
            .Where(f => string.IsNullOrEmpty(filter) ||
                        f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name)
            .Take(15);

        foreach (var (filePath, name) in files)
        {
            var btn = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 8, 16, 8),
                Background = Brushes.Transparent,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = filePath
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = $"\U0001F4C4 {name}",
                FontSize = 13, FontWeight = FontWeights.SemiBold
            });
            sp.Children.Add(new TextBlock
            {
                Text = Path.GetRelativePath(_vaultPath, filePath),
                FontSize = 9, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontFamily = (FontFamily)FindResource("MonoFont")
            });
            btn.Content = sp;
            btn.Click += (_, _) =>
            {
                QuickSwitcherOverlay.Visibility = Visibility.Collapsed;
                OpenFileInEditor(filePath);
            };
            // Hover effect
            btn.MouseEnter += (_, _) => btn.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x3A));
            btn.MouseLeave += (_, _) => btn.Background = Brushes.Transparent;
            QuickSwitcherResults.Children.Add(btn);
        }

        if (!QuickSwitcherResults.Children.OfType<Button>().Any())
        {
            QuickSwitcherResults.Children.Add(new TextBlock
            {
                Text = "No notes found",
                FontSize = 12, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                Margin = new Thickness(16, 12, 16, 12), FontStyle = FontStyles.Italic
            });
        }
    }

    // ═══════════════════════════════════════
    // VAULT FILE MANAGEMENT
    // ═══════════════════════════════════════

    private void VaultTree_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (VaultTree.SelectedItem is TreeViewItem item && item.Tag is string filePath)
            OpenFileInEditor(filePath);
    }

    private void VaultOpenInEditor_Click(object s, RoutedEventArgs e)
    {
        if (VaultTree.SelectedItem is TreeViewItem item && item.Tag is string filePath)
            OpenFileInEditor(filePath);
    }

    private void VaultNewNote_Click(object s, RoutedEventArgs e) => CreateNewNote();

    private void VaultNewFolder_Click(object s, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "New Folder", Width = 400, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0))
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "Folder Name:", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox
        {
            FontSize = 14, Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A))
        };
        sp.Children.Add(tb);
        var okBtn = new Button
        {
            Content = "Create", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(0, 240, 255)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x1A)), FontWeight = FontWeights.Bold
        };
        okBtn.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        tb.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) { okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        sp.Children.Add(okBtn);
        dialog.Content = sp;

        if (dialog.ShowDialog() == true)
        {
            var name = tb.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var dirPath = Path.Combine(_vaultPath, name);
            if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
            RefreshVaultTree();
            StatusText.Text = $"Folder created: {name}";
        }
    }

    private void VaultRename_Click(object s, RoutedEventArgs e)
    {
        if (VaultTree.SelectedItem is not TreeViewItem item || item.Tag is not string filePath) return;

        var oldName = Path.GetFileNameWithoutExtension(filePath);
        var dialog = new Window
        {
            Title = "Rename", Width = 400, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0))
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "New Name:", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox
        {
            Text = oldName, FontSize = 14, Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A))
        };
        tb.SelectAll();
        sp.Children.Add(tb);
        var okBtn = new Button
        {
            Content = "Rename", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(0, 240, 255)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x1A)), FontWeight = FontWeights.Bold
        };
        okBtn.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        tb.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) { okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        sp.Children.Add(okBtn);
        dialog.Content = sp;

        if (dialog.ShowDialog() == true)
        {
            var newName = tb.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == oldName) return;
            var dir = Path.GetDirectoryName(filePath)!;
            var newPath = Path.Combine(dir, newName + ".md");
            if (File.Exists(newPath))
            {
                MessageBox.Show("A file with that name already exists.", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            File.Move(filePath, newPath);

            // Update wiki-links across vault
            UpdateWikiLinks(oldName, newName);

            if (_mdEditor.CurrentFilePath == filePath)
            {
                _mdEditor.OpenFile(newPath);
                EditorFileTitle.Text = newName;
            }
            RefreshVaultTree();
            StatusText.Text = $"Renamed: {oldName} -> {newName}";
        }
    }

    private void VaultDelete_Click(object s, RoutedEventArgs e)
    {
        if (VaultTree.SelectedItem is not TreeViewItem item || item.Tag is not string filePath) return;
        var name = Path.GetFileName(filePath);
        var result = MessageBox.Show($"Delete \"{name}\"?\n\nThis cannot be undone.",
            "Delete Note", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        File.Delete(filePath);
        RefreshVaultTree();
        StatusText.Text = $"Deleted: {name}";
    }

    private void VaultRefresh_Click(object s, RoutedEventArgs e)
    {
        RefreshVaultTree();
        IndexVault();
        _dashPhysics.LoadFromGraphDiff(_graph);
        _graphPhysics.LoadFromGraphDiff(_graph);
        UpdateUI();
        StatusText.Text = "Vault refreshed";
    }

    /// <summary>Update all [[wiki-links]] in the vault when a note is renamed.</summary>
    private void UpdateWikiLinks(string oldName, string newName)
    {
        foreach (var file in Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var updated = Regex.Replace(content, $@"\[\[{Regex.Escape(oldName)}(\|[^\]]+)?\]\]",
                    m => $"[[{newName}{m.Groups[1].Value}]]", RegexOptions.IgnoreCase);
                if (updated != content)
                    File.WriteAllText(file, updated);
            }
            catch (IOException ex) { Debug.WriteLine($"Wiki-link update skipped for {file}: {ex.Message}"); }
        }
    }

    private void RefreshVaultTree() => PopulateVaultTree();
}
