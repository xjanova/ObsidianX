# ObsidianX — Neural Knowledge Network

<div align="center">

```
   ____  _         _     _ _             __  __
  / __ \| |__  ___(_) __| (_) __ _ _ __ \ \/ /
 | |  | | '_ \/ __| |/ _` | |/ _` | '_ \ \  /
 | |__| | |_) \__ \ | (_| | | (_| | | | |/  \
  \____/|_.__/|___/_|\__,_|_|\__,_|_| |_/_/\_\
```

**A futuristic 3D knowledge management system with peer-to-peer brain sharing**

**ObsidianX is a complete Obsidian replacement with superpowers: 3D visualization, P2P knowledge sharing, and AI integration.**

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/WPF-3D%20Viewport-blue)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
[![SignalR](https://img.shields.io/badge/SignalR-Real--time-green)](https://learn.microsoft.com/en-us/aspnet/core/signalr/)
[![License](https://img.shields.io/badge/License-MIT-cyan)](LICENSE)

</div>

---

## What is ObsidianX?

ObsidianX transforms your Markdown vault into a **living, interactive 3D brain** — visualizing your knowledge as physics-based nodes you can explore, edit, share, and grow.

It pairs a **WPF desktop client** (cyberpunk-themed, with full Markdown editor and 3D graph visualization) with a **matchmaking server** that connects knowledge brains across a network, enabling peer-to-peer expertise sharing.

**ObsidianX does everything Obsidian does — and more.**

## ObsidianX vs Obsidian

| Feature | Obsidian | ObsidianX |
|---------|----------|-----------|
| Markdown Editor | Yes | Yes (AvalonEdit + syntax highlighting) |
| Live Preview | Yes | Yes (cyberpunk-styled) |
| Auto-save | Yes | Yes (3-second delay) |
| [[Wiki-link]] Navigation | Yes | Yes (Ctrl+Click) |
| Backlinks | Yes | Yes (panel with context) |
| Full-text Search | Yes | Yes (highlighted results) |
| Quick Switcher | Yes (Ctrl+O) | Yes (Ctrl+O) |
| File Management | Yes | Yes (create/rename/delete + wiki-link auto-update) |
| Graph View | 2D flat | **3D physics-based with orbit camera** |
| P2P Brain Sharing | No | **Yes (SignalR real-time)** |
| Expertise Matching | No | **Yes (find experts by category)** |
| Crypto Identity | No | **Yes (ECDSA wallet-style addresses)** |
| Claude AI Integration | No | **Yes (built-in)** |
| Server Dashboard | No | **Yes (cyberpunk web UI)** |
| Growth Tracking | No | **Yes (per-category bar chart)** |

## Features

### Markdown Editor
- Full-featured editor with **AvalonEdit** (syntax highlighting, line numbers, word wrap)
- **Split view**: editor + live preview side by side
- **Auto-save** after 3 seconds of inactivity
- **Cyberpunk-styled preview** with colored headings, code blocks, task lists, wiki-links
- **Toolbar**: H1, H2, Bold, Italic, WikiLink, Code Block, Task List
- **Keyboard shortcuts**: Ctrl+S Save, Ctrl+B Bold, Ctrl+I Italic, Ctrl+K WikiLink, Ctrl+F Find
- **YAML frontmatter** support (hidden in preview, highlighted in editor)

### Wiki-links & Backlinks
- **Ctrl+Click** any `[[wiki-link]]` to navigate to that note
- **Auto-create**: clicking a broken link offers to create the note
- **Backlinks panel**: shows which notes reference the current note with surrounding context
- **Auto-update**: renaming a note updates all `[[wiki-links]]` across the vault

### File Management
- **Create** notes and folders from the UI
- **Rename** with automatic wiki-link update across vault
- **Delete** with confirmation dialog
- **Quick Switcher** (Ctrl+O): fuzzy search to open any note instantly
- **Full-text search** across all vault notes with highlighted match context

### 3D Brain Visualization
- Interactive **physics-based** node graph (Coulomb repulsion, spring forces, damping)
- **Click nodes** to preview content, **drag to orbit**, **scroll to zoom**
- **Batched mesh rendering** for performance at scale (LOD switching at 100+ nodes)
- **Color-coded** by knowledge category (25 categories)
- **FPS counter** and energy monitor for performance tracking

### Crypto-Style Brain Identity
- **ECDSA-based** identity generation (wallet-like addresses: `0xBRAIN-xxxx-xxxx-xxxx-xxxx`)
- **Sign and verify** knowledge authenticity
- Persistent identity stored locally

### Knowledge Indexing
- Automatic Markdown vault scanning with **YAML frontmatter** extraction
- **Wiki-link** `[[detection]]` and `#hashtag` parsing
- **25 knowledge categories** with keyword-based classification
- **Expertise scoring** and growth tracking per category

### Peer-to-Peer Network
- **SignalR real-time** connections between brains
- **Expertise matching** — find brains that know what you need
- **Knowledge sharing** with consent (request/accept/reject flow)
- **BitTorrent-like model**: data stays local, shared on demand

### Server Dashboard
- **Cyberpunk web UI** at `http://localhost:5142`
- **Real-time** connected brains, activity feed, network stats
- **Expertise map** visualization with category bubbles
- **XSS-protected** output with HTML escaping

### Claude AI Integration
- Generates `CLAUDE.md` with your brain profile and expertise
- Your vault becomes Claude's "second brain"
- **Direct Claude queries** from the app with streaming output

## Architecture

```
ObsidianX/
├── ObsidianX.Core/              # Shared models & services (.NET 9)
│   ├── Models/
│   │   ├── BrainIdentity.cs        # ECDSA crypto identity
│   │   ├── KnowledgeNode.cs        # Graph nodes & edges
│   │   ├── KnowledgeCategory.cs    # 25 categories + scoring
│   │   └── PeerInfo.cs             # Peer & sharing models
│   └── Services/
│       ├── KnowledgeIndexer.cs     # Vault scanner & categorizer
│       └── ClaudeIntegration.cs    # Claude AI bridge
│
├── ObsidianX.Client/            # WPF Desktop App (.NET 10)
│   ├── MainWindow.xaml/cs          # Full UI: 11 views + 3D viewport
│   ├── Editor/
│   │   ├── MarkdownEditor.cs       # Editor logic, preview, backlinks
│   │   └── MarkdownHighlighting.xshd # Syntax highlighting rules
│   ├── Themes/CyberpunkTheme.xaml  # Neon dark theme
│   └── Services/
│       ├── PhysicsEngine.cs        # Force-directed graph
│       └── NetworkClient.cs        # SignalR client
│
└── ObsidianX.Server/            # ASP.NET Core Server (.NET 10)
    ├── Program.cs                  # Startup + REST API
    ├── Hubs/BrainHub.cs            # SignalR matchmaking hub
    └── wwwroot/index.html          # Web dashboard
```

### Client Views (11 total)

| View | Description |
|------|-------------|
| Dashboard | Stats, mini 3D brain, expertise bars, Claude status, quick actions |
| Editor | Split Markdown editor + live preview + backlinks |
| Brain Graph | Full-screen 3D with shake/reset, FPS counter, node info panel |
| Vault Explorer | Tree view with create/rename/delete, context menu |
| Search | Full-text search with highlighted results |
| Network | Connect/disconnect, expertise matching, results list |
| Claude AI | Chat interface for Claude queries |
| Peers | Online peers with avatar, address, expertise tags |
| Sharing | Incoming requests (accept/reject) + sharing history |
| Growth | Bar chart showing expertise distribution per category |
| Settings | Brain name, vault path, server URL configuration |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or .NET 9 for Core project)
- [Visual Studio 2026](https://visualstudio.microsoft.com/) (recommended) or VS Code
- Windows 10/11 (WPF requirement)

## Getting Started

### 1. Clone

```bash
git clone https://github.com/xjanova/ObsidianX.git
cd ObsidianX
```

### 2. Build

```bash
dotnet build ObsidianX.slnx
```

### 3. Run the Server

```bash
cd ObsidianX.Server
dotnet run
```

Open `http://localhost:5142` in your browser to see the server dashboard.

### 4. Run the Client

```bash
cd ObsidianX.Client
dotnet run
```

Or pass a custom vault path:

```bash
dotnet run -- "C:\path\to\your\vault"
```

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+S` | Save current note |
| `Ctrl+O` | Quick Switcher (open any note) |
| `Ctrl+N` | Create new note |
| `Ctrl+B` | Toggle bold |
| `Ctrl+I` | Toggle italic |
| `Ctrl+K` | Insert wiki-link |
| `Ctrl+F` | Find in editor |
| `Ctrl+Click` | Follow wiki-link |

## Development

### Building from Visual Studio

1. Open `ObsidianX.slnx` in Visual Studio 2026
2. Set **ObsidianX.Client** as the startup project
3. Press F5 to run

### Running Both (Server + Client)

For the full network experience, run both:

```bash
# Terminal 1
cd ObsidianX.Server && dotnet run

# Terminal 2
cd ObsidianX.Client && dotnet run
```

### Project Structure

| Project | Target | Description |
|---------|--------|-------------|
| `ObsidianX.Core` | .NET 9 | Shared models, indexer, Claude integration |
| `ObsidianX.Client` | .NET 10 (Windows) | WPF desktop app with editor + 3D visualization |
| `ObsidianX.Server` | .NET 10 | ASP.NET Core + SignalR matchmaking hub + web dashboard |

### Security

- **ECDSA signatures** for brain identity and knowledge authenticity
- **Input validation** on all SignalR hub methods
- **XSS protection** in server dashboard (HTML escaping)
- **Thread-safe** concurrent peer management with proper locking
- **No command injection** — Claude CLI uses ArgumentList (not string interpolation)
- **Auto-save** prevents data loss

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/my-feature`
3. Commit your changes: `git commit -m "Add my feature"`
4. Push to the branch: `git push origin feature/my-feature`
5. Open a Pull Request

See [CONTRIBUTING.md](CONTRIBUTING.md) for detailed guidelines.

## Roadmap

- [ ] Full knowledge content sharing (encrypted P2P transfer)
- [ ] Multiple vault support
- [ ] Plugin system for custom categorizers
- [ ] Voice-to-knowledge (audio note capture)
- [ ] Mobile companion app
- [ ] Decentralized server discovery (DHT)
- [ ] AI-powered knowledge recommendations
- [ ] Collaborative editing (real-time co-authoring)
- [ ] Image/attachment support in editor
- [ ] Custom themes and layouts

## License

MIT License. See [LICENSE](LICENSE) for details.

---

<div align="center">

**Built with neural passion by the ObsidianX team**

*Your knowledge, visualized. Your brain, connected.*

</div>
