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

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/WPF-3D%20Viewport-blue)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
[![SignalR](https://img.shields.io/badge/SignalR-Real--time-green)](https://learn.microsoft.com/en-us/aspnet/core/signalr/)
[![License](https://img.shields.io/badge/License-MIT-cyan)](LICENSE)

</div>

---

## What is ObsidianX?

ObsidianX transforms your [Obsidian](https://obsidian.md/) vault into a **living, interactive 3D brain** — visualizing your knowledge as physics-based nodes you can explore, share, and grow.

It pairs a **WPF desktop client** (cyberpunk-themed, with 3D graph visualization) with a **matchmaking server** that connects knowledge brains across a network, enabling peer-to-peer expertise sharing.

## Features

### 3D Brain Visualization
- Interactive physics-based node graph (Coulomb repulsion, spring forces, damping)
- Click nodes to preview content, drag to orbit, scroll to zoom
- Batched mesh rendering for performance at scale (LOD switching at 100+ nodes)
- Color-coded by knowledge category

### Crypto-Style Brain Identity
- ECDSA-based identity generation (wallet-like addresses: `0xBRAIN-xxxx-xxxx-xxxx-xxxx`)
- Sign and verify knowledge authenticity
- Persistent identity stored locally

### Knowledge Indexing
- Automatic Markdown vault scanning with YAML frontmatter extraction
- Wiki-link `[[detection]]` and `#hashtag` parsing
- 25 knowledge categories with keyword-based classification
- Expertise scoring and growth tracking

### Peer-to-Peer Network
- SignalR real-time connections between brains
- Expertise matching — find brains that know what you need
- Knowledge sharing with consent (request/accept/reject flow)
- BitTorrent-like model: data stays local, shared on demand

### Server Dashboard
- Cyberpunk web UI at `http://localhost:5142`
- Real-time connected brains, activity feed, network stats
- Expertise map visualization

### Claude AI Integration
- Generates `CLAUDE.md` with your brain profile and expertise
- Your vault becomes Claude's "second brain"
- Direct Claude queries from the app

## Architecture

```
ObsidianX/
├── ObsidianX.Core/          # Shared models & services (.NET 9)
│   ├── Models/
│   │   ├── BrainIdentity.cs    # ECDSA crypto identity
│   │   ├── KnowledgeNode.cs    # Graph nodes & edges
│   │   ├── KnowledgeCategory.cs # 25 categories + scoring
│   │   └── PeerInfo.cs          # Peer & sharing models
│   └── Services/
│       ├── KnowledgeIndexer.cs  # Vault scanner & categorizer
│       └── ClaudeIntegration.cs # Claude AI bridge
│
├── ObsidianX.Client/        # WPF Desktop App (.NET 10)
│   ├── MainWindow.xaml/cs      # Full UI + 3D viewport
│   ├── Themes/CyberpunkTheme.xaml
│   └── Services/
│       ├── PhysicsEngine.cs    # Force-directed graph
│       └── NetworkClient.cs    # SignalR client
│
└── ObsidianX.Server/        # ASP.NET Core Server (.NET 10)
    ├── Program.cs              # Startup + REST API
    ├── Hubs/BrainHub.cs        # SignalR matchmaking hub
    └── wwwroot/index.html      # Web dashboard
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or .NET 9 for Core project)
- [Visual Studio 2026](https://visualstudio.microsoft.com/) (recommended) or VS Code
- [Obsidian](https://obsidian.md/) (for vault management)
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
| `ObsidianX.Client` | .NET 10 (Windows) | WPF desktop app with 3D visualization |
| `ObsidianX.Server` | .NET 10 | ASP.NET Core + SignalR matchmaking hub |

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/my-feature`
3. Commit your changes: `git commit -m "Add my feature"`
4. Push to the branch: `git push origin feature/my-feature`
5. Open a Pull Request

### Development Guidelines

- Follow C# naming conventions (PascalCase for public, _camelCase for private fields)
- Keep physics updates in `PhysicsEngine.cs`, rendering in `MainWindow.xaml.cs`
- Server hub methods go in `BrainHub.cs`
- All shared models belong in `ObsidianX.Core`
- Test with 100+ nodes to verify performance

## Roadmap

- [ ] Full knowledge content sharing (encrypted P2P transfer)
- [ ] Multiple vault support
- [ ] Plugin system for custom categorizers
- [ ] Voice-to-knowledge (audio note capture)
- [ ] Mobile companion app
- [ ] Decentralized server discovery (DHT)
- [ ] AI-powered knowledge recommendations
- [ ] Collaborative editing (real-time co-authoring)

## License

MIT License. See [LICENSE](LICENSE) for details.

---

<div align="center">

**Built with neural passion by the ObsidianX team**

*Your knowledge, visualized. Your brain, connected.*

</div>
