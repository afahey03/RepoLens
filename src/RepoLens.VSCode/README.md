# RepoLens for VS Code

**Understand any GitHub repository in seconds — right inside VS Code.**

RepoLens analyzes GitHub repositories and gives you an interactive overview, architecture graph, symbol tree, and intelligent code search without leaving your editor.

<img width="1557" height="973" alt="image" src="https://github.com/user-attachments/assets/3a750ea6-2e9a-4268-a168-0c966d58edd2" />

---

## Features

### Repository Overview
View language breakdown, key types, most connected modules, external dependencies, and a generated plain-English summary.

### Architecture Graph
Interactive force-directed dependency graph showing how files, classes, and modules connect. Double-click any node to navigate to the source.

### Symbol Tree
Browse all extracted symbols (classes, interfaces, functions, methods, properties) organized by file. Click any symbol to jump to its definition.

### Code Search
BM25-ranked search across all symbols with kind filtering. Find classes, functions, interfaces, and more instantly.

### Auto-Start API Server
The extension automatically discovers and starts the RepoLens API server — no manual setup required.

### Workspace Auto-Detection
Detects Git repositories in your workspace and offers to analyze them on activation.

---

## Getting Started

1. Open a workspace that contains a Git repository
2. The extension will prompt you to analyze it, or use the Command Palette:
   - **RepoLens: Analyze Current Workspace** — analyzes the repo in your open workspace
   - **RepoLens: Analyze GitHub Repository** — paste any GitHub URL to analyze
3. Once analysis completes, explore via the **RepoLens** sidebar panel

---

## Commands

| Command | Description |
|---------|-------------|
| `RepoLens: Analyze GitHub Repository` | Analyze any GitHub repo by URL |
| `RepoLens: Analyze Current Workspace` | Analyze the Git repo in your workspace |
| `RepoLens: Show Overview` | Open the repository overview panel |
| `RepoLens: Show Architecture Graph` | Open the interactive architecture graph |
| `RepoLens: Search Symbols` | Open the symbol search panel |
| `RepoLens: Refresh Symbol Tree` | Reload the symbol tree |

---

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `repolens.apiBaseUrl` | `http://localhost:5000` | Base URL of the RepoLens API server |
| `repolens.apiProjectPath` | _(empty)_ | Path to the RepoLens.Api project (for auto-start) |
| `repolens.autoStartServer` | `true` | Automatically start the API server if not running |
| `repolens.gitHubToken` | _(empty)_ | GitHub PAT for private repositories |
| `repolens.openAiApiKey` | _(empty)_ | OpenAI API key for AI-powered summaries |

---

## Supported Languages

RepoLens includes parsers for 21 languages: C#, TypeScript, JavaScript, Python, Java, Go, C, C++, Swift, Rust, SQL, Scala, Kotlin, PHP, Ruby, Dart, Lua, Perl, R, Haskell, and Elixir.

---

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for the API server)
- The RepoLens API backend (auto-started by the extension, or run manually)

---

## Links

- [GitHub Repository](https://github.com/afahey03/RepoLens)
- [Report Issues](https://github.com/afahey03/RepoLens/issues)

---

**Author:** Aidan Fahey
