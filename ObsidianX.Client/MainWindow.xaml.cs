using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
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

    // Node selection
    private int? _selectedNodeDash;
    private int? _selectedNodeGraph;

    // Pre-built sphere mesh (shared for performance)
    private static readonly MeshGeometry3D SharedSphere = BuildUnitSphere(10, 6);
    private static readonly MeshGeometry3D SharedSphereLOD = BuildUnitSphere(6, 4);

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
        IndexVault();
        CheckClaudeConnection();

        // Load physics
        _dashPhysics.LoadFromGraph(_graph);
        _dashPhysics.Disturb(0.5);
        _graphPhysics.LoadFromGraph(_graph);
        _graphPhysics.Disturb(0.8);

        // Populate UI
        UpdateUI();
        PopulateMatchCategories();
        PopulateSettings();

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
        _mdEditor.FileSaved += f => { StatusText.Text = $"Saved: {Path.GetFileName(f)}"; IndexVault(); };
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
            GraphFPS.Text = $"{_frameCount} FPS | {_graphPhysics.Nodes.Count} nodes | E={_graphPhysics.TotalEnergy:F2}";
            _frameCount = 0;
            _lastFpsTime = now;
        }

        // Step physics
        _dashPhysics.Step();
        _graphPhysics.Step();

        // Rebuild 3D meshes (optimized: single batched geometry)
        if (DashboardView.Visibility == Visibility.Visible)
        {
            UpdateCamera(DashCam, _camYaw, _camPitch, _camDist);
            RebuildScene(BrainModel, _dashPhysics, _selectedNodeDash);
        }

        if (BrainGraphView.Visibility == Visibility.Visible)
        {
            UpdateCamera(GraphCam, _graphYaw, _graphPitch, _graphDist);
            RebuildScene(FullGraphModel, _graphPhysics, _selectedNodeGraph);
        }
    }

    // ═══════════════════════════════════════
    // OPTIMIZED 3D RENDERING — batched mesh
    // ═══════════════════════════════════════
    private void RebuildScene(ModelVisual3D parent, PhysicsEngine physics, int? selectedIdx)
    {
        var group = new Model3DGroup();

        if (physics.Nodes.Count == 0)
        {
            BuildPlaceholderBrain(group);
            parent.Content = group;
            return;
        }

        // --- BATCH ALL NODES INTO ONE MESH PER MATERIAL ---
        // Group nodes by category color for minimal draw calls
        var colorGroups = new Dictionary<Color, (MeshGeometry3D mesh, bool emissive)>();
        var glowGroup = new MeshGeometry3D(); // selected/hovered glow

        for (int i = 0; i < physics.Nodes.Count; i++)
        {
            var node = physics.Nodes[i];
            var color = GetCategoryColor(node.Category);
            var isSelected = i == selectedIdx;
            var pulse = 1.0 + Math.Sin(_time * 3 + node.PulsePhase) * 0.08;
            var radius = node.Radius * pulse;

            if (isSelected || node.IsHovered)
                radius *= 1.4; // bigger when selected

            // Pick LOD based on node count
            var sphereMesh = physics.Nodes.Count > 100 ? SharedSphereLOD : SharedSphere;

            if (!colorGroups.ContainsKey(color))
                colorGroups[color] = (new MeshGeometry3D(), false);

            AppendSphereToMesh(colorGroups[color].mesh, node.Position, radius, sphereMesh);

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

        // Glow for selected
        if (glowGroup.Positions.Count > 0)
        {
            var glowMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(30, 0, 240, 255)));
            group.Children.Add(new GeometryModel3D(glowGroup, glowMat));
        }

        // --- BATCH ALL EDGES INTO ONE MESH ---
        var edgeMesh = new MeshGeometry3D();
        var nodeIndex = physics.Nodes.ToDictionary(n => n.Id, n => n);

        foreach (var edge in physics.Edges)
        {
            if (!nodeIndex.TryGetValue(edge.SourceId, out var src)) continue;
            if (!nodeIndex.TryGetValue(edge.TargetId, out var tgt)) continue;
            AppendLineToMesh(edgeMesh, src.Position, tgt.Position, 0.012);
        }

        if (edgeMesh.Positions.Count > 0)
        {
            var edgeMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(50, 0, 240, 255)));
            group.Children.Add(new GeometryModel3D(edgeMesh, edgeMat));
        }

        parent.Content = group;
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
    {
        double yawRad = yaw * Math.PI / 180;
        double pitchRad = pitch * Math.PI / 180;
        var pos = new Point3D(
            dist * Math.Sin(yawRad) * Math.Cos(pitchRad),
            dist * Math.Sin(pitchRad),
            dist * Math.Cos(yawRad) * Math.Cos(pitchRad));
        cam.Position = pos;
        cam.LookDirection = new Vector3D(-pos.X, -pos.Y, -pos.Z);
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
        var hit = HitTestNode(FullGraphViewport, GraphCam, _graphPhysics, mouseOnViewport);
        if (hit.HasValue)
        {
            _selectedNodeGraph = hit;
            ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot, GraphNodeContent, _graphPhysics.Nodes[hit.Value]);
        }
    }

    private void FullGraph_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ((UIElement)s).ReleaseMouseCapture();
    }

    private void FullGraph_MouseMove(object s, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition((IInputElement)s);
        var dx = pos.X - _lastMouse.X;
        var dy = pos.Y - _lastMouse.Y;
        _graphYaw += dx * 0.5;
        _graphPitch = Math.Clamp(_graphPitch + dy * 0.3, -80, 80);
        _lastMouse = pos;
    }

    private void FullGraph_MouseWheel(object s, MouseWheelEventArgs e)
    {
        _graphDist = Math.Clamp(_graphDist - e.Delta * 0.008, 4, 40);
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

        return physics.HitTest(rayOrigin, rayDir, 0.5);
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
            catch { }
        }
    }

    private void ShakeGraph_Click(object sender, RoutedEventArgs e) => _graphPhysics.Disturb(2.0);
    private void ResetCamera_Click(object sender, RoutedEventArgs e) { _graphYaw = 0; _graphPitch = 15; _graphDist = 14; }

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
        _graph = _indexer.IndexVault(_vaultPath);
        StatusText.Text = $"Indexed {_graph.TotalNodes} nodes";
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
        _dashPhysics.LoadFromGraph(_graph);
        _dashPhysics.Disturb(1.0);
        _graphPhysics.LoadFromGraph(_graph);
        _graphPhysics.Disturb(1.0);
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
        await _network.DisconnectAsync();
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

        var results = await _network.FindExpertsAsync(new MatchRequest
        {
            RequesterAddress = _identity.Address,
            DesiredCategory = category,
            MinExpertiseScore = 0.1,
            MaxResults = 10
        });

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
        await _network.RequestShareAsync(new ShareRequest
        {
            FromAddress = _identity.Address,
            ToAddress = targetAddress,
            NodeTitle = "Knowledge Exchange",
            Category = KnowledgeCategory.Other,
            WordCount = (int)_graph.TotalWords,
            Signature = _identity.Sign(targetAddress)
        });
        _shareHistory.Add($"[SENT] Request to {targetAddress[..20]}...");
        StatusText.Text = "Share request sent!";
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
        ClaudeOutput.Text += $"\n\n> YOU: {q}\n\nClaude is thinking...";
        ClaudeInput.Text = "";
        try
        {
            var r = await _claude.QueryClaude(q);
            ClaudeOutput.Text = ClaudeOutput.Text.Replace("Claude is thinking...", $"CLAUDE: {r}");
        }
        catch (Exception ex)
        {
            ClaudeOutput.Text = ClaudeOutput.Text.Replace("Claude is thinking...", $"[Error: {ex.Message}]");
        }
    }

    private void ClaudeInput_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) AskClaude_Click(s, e); }

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
        catch { }
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
        StatusText.Text = $"Server URL updated to: {url}";
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
            catch { }
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
        _dashPhysics.LoadFromGraph(_graph);
        _graphPhysics.LoadFromGraph(_graph);
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
            catch { }
        }
    }

    private void RefreshVaultTree() => PopulateVaultTree();
}
