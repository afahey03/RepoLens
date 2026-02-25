# RepoLens

**Understand any GitHub repository in seconds.**

RepoLens analyzes public (and private, with a PAT) GitHub repositories and generates an interactive overview, architecture graph, and intelligent code search — all from a single URL. Available as a web app and a VS Code extension.

---

## Features

- **Repository Overview** — language breakdown (lines of code), frameworks detected, entry points, key types, external dependencies, complexity classification, and a generated plain-English summary (optionally LLM-powered)
- **Interactive Architecture Graph** — dagre-layouted dependency graph with zoom, pan, filtering by node/edge type, node detail sidebar, minimap, and legend (React Flow on web; SVG force-directed in VS Code)
- **BM25 Code Search** — full-text search across all symbols with kind filtering, pagination, autocomplete suggestions, and debounced input
- **21 Language Parsers** — C# (Roslyn AST), TypeScript, JavaScript, Python, Java, Go, C, C++, Swift, Rust, SQL, Scala, Kotlin, PHP, Ruby, Dart, Lua, Perl, R, Haskell, Elixir
- **Private Repo Support** — GitHub PAT authentication for private and org repositories
- **Incremental Re-analysis** — hash-based change detection; only re-parses changed files
- **LLM-Powered Summaries** — richer natural-language project descriptions via OpenAI API
- **PR Impact Analysis** — highlight graph changes from a pull request diff
- **VS Code Extension** — in-editor overview panel, architecture view, search, symbol tree, auto-start API server, workspace auto-detection
- **Persistent Disk Cache** — analyzed repos survive restarts; backed by JSON on disk
- **Download Resilience** — exponential-backoff retries for GitHub downloads
- **Progressive Loading** — real-time progress bar with stage labels during analysis
- **Docker Support** — single-command production deployment via Docker Compose

---

## Pictures

<img width="1011" height="807" alt="image" src="https://github.com/user-attachments/assets/37e43c6a-5b2b-4da5-a4bf-1ef5fbd9dfff" />

<img width="996" height="905" alt="image" src="https://github.com/user-attachments/assets/5a7a3f65-0533-42fb-915f-b84ac090fbf5" />

<img width="1074" height="823" alt="image" src="https://github.com/user-attachments/assets/49c5a94a-62b1-43b1-9957-a70300718923" />

<img width="1067" height="880" alt="Screenshot 2026-02-22 212409" src="https://github.com/user-attachments/assets/2622354d-35a8-4bb2-b1a5-489dcb18455d" />

<img width="1557" height="973" alt="image" src="https://github.com/user-attachments/assets/3a750ea6-2e9a-4268-a168-0c966d58edd2" />



---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend | .NET 8 / ASP.NET Core Web API |
| Frontend | React, Vite, TypeScript |
| Graph (Web) | @xyflow/react, @dagrejs/dagre |
| Graph (VS Code) | SVG force-directed layout |
| C# Parsing | Microsoft.CodeAnalysis (Roslyn) |
| Search | Custom BM25 inverted index |
| VS Code Extension | TypeScript, webpack, VS Code API |
| Packaging | Docker multi-stage build |

---

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org/)

### Development

```bash
# 1. Start the API (from repo root)
cd src/RepoLens.Api
dotnet run --urls "http://localhost:5000"

# 2. Start the frontend (separate terminal)
cd src/RepoLens.Web
npm install
npm run dev
```

Open **http://localhost:5173**, paste a public GitHub repo URL, and click **Analyze**.

### Docker

```bash
# Build and run with Docker Compose
docker compose up --build

# Access at http://localhost:5000
```

The cache is persisted via a named Docker volume (`repolens-cache`).

### VS Code Extension

```bash
# Install the pre-built VSIX
code --install-extension src/RepoLens.VSCode/repolens-0.1.0.vsix

# Or build from source
cd src/RepoLens.VSCode
npm install
npm run build
npx @vscode/vsce package
code --install-extension repolens-0.1.0.vsix
```

The extension auto-starts the API server and auto-detects workspace repositories. Use the **RepoLens** sidebar or run commands from the Command Palette (`Ctrl+Shift+P` → "RepoLens").

---

## Project Structure

```
├── architecture.md             # Detailed architecture documentation
├── docs/
│   └── vision.md               # Product vision and roadmap
├── docker-compose.yml          # Production Docker Compose config
├── Dockerfile                  # Multi-stage Docker build
└── src/
    ├── RepoLens.Api/           # ASP.NET Core Web API (controllers, Program.cs)
    ├── RepoLens.Analysis/      # Repo downloading, file scanning, 21 language parsers
    │   └── Parsers/            # C#, JS/TS, Python, Java, Go, C, C++, Swift, Rust,
    │                           # SQL, Scala, Kotlin, PHP, Ruby, Dart, Lua, Perl,
    │                           # R, Haskell, Elixir (+ Roslyn C# parser)
    ├── RepoLens.Engine/        # BM25 search engine, disk cache, dependency graph
    ├── RepoLens.Shared/        # Contracts, DTOs, models shared across projects
    ├── RepoLens.Web/           # React + Vite frontend (overview, graph, search)
    ├── RepoLens.VSCode/        # VS Code extension (overview, architecture, search,
    │   ├── src/                #   symbol tree, server manager, repo auto-detection)
    │   └── media/              #   Icons (SVG sidebar icon, PNG marketplace icon)
    └── RepoLens.slnx           # .NET solution file
```

---

## API Reference

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/repository/analyze` | Start analysis (returns immediately; poll for progress) |
| GET | `/api/repository/{id}/progress` | Poll analysis progress |
| GET | `/api/repository/{id}/overview` | Repository overview |
| GET | `/api/repository/{id}/architecture` | Architecture graph (nodes + edges) |
| GET | `/api/repository/{id}/architecture/stats` | Graph statistics |
| GET | `/api/repository/{id}/search?q=...` | BM25 code search (supports `kinds`, `skip`, `take`) |
| GET | `/api/repository/{id}/suggest?q=...` | Autocomplete suggestions |

---

## Supported Languages

| Language | Parser | Capabilities |
|----------|--------|-------------|
| C# | Roslyn AST (`Microsoft.CodeAnalysis`) | Namespaces, classes, interfaces, records, structs, enums, methods, properties, usings |
| TypeScript | Regex-based | Classes, functions, interfaces, type aliases, enums, imports |
| JavaScript | Regex-based | Classes, functions (declarations + arrows), imports, require |
| Python | Regex-based | Classes, functions/methods, decorators, imports, constants |
| Java | Regex-based | Classes, interfaces, methods, enums, annotations, packages |
| Go | Regex-based | Structs, interfaces, functions, receiver methods, packages |
| C | Regex-based | Functions, structs, enums, typedefs, macros, globals |
| C++ | Regex-based | Classes, structs, functions, methods, templates, namespaces |
| Swift | Regex-based | Classes, structs, enums, protocols, functions, extensions |
| Rust | Regex-based | Structs, enums, traits, functions, impls, modules, macros |
| SQL | Regex-based | Tables, views, procedures, functions, triggers, indexes |
| Scala | Regex-based | Classes, traits, objects, case classes, defs, vals |
| Kotlin | Regex-based | Classes, data classes, objects, interfaces, functions |
| PHP | Regex-based | Classes, interfaces, traits, functions, namespaces |
| Ruby | Regex-based | Classes, modules, methods, blocks |
| Dart | Regex-based | Classes, mixins, extensions, functions, enums |
| Lua | Regex-based | Functions, local functions, table methods |
| Perl | Regex-based | Packages, subroutines, methods |
| R | Regex-based | Functions, S4 classes, R6 classes, methods |
| Haskell | Regex-based | Modules, data types, type classes, functions, instances |
| Elixir | Regex-based | Modules, functions, macros, protocols, structs |

---

## Completed Milestones

All 8 original build phases and 6 near-term priorities are **complete**:

1. ~~Private repo support~~ **DONE** — GitHub PAT authentication
2. ~~Incremental re-analysis~~ **DONE** — hash-based change detection
3. ~~LLM-powered summaries~~ **DONE** — OpenAI API integration
4. ~~Roslyn-based C# parser~~ **DONE** — AST-level accuracy via Microsoft.CodeAnalysis
5. ~~PR impact analysis~~ **DONE** — graph diff highlighting from PR URLs
6. ~~VS Code extension~~ **DONE** — overview, architecture, search, symbol tree, auto-start server

---

## Next Stages

The following features are planned for future development:

| Priority | Feature | Description |
|----------|---------|-------------|
| 🔥 | **Dependency risk visualization** | Fan-in/fan-out heat mapping to identify high-risk modules |
| 🔥 | **Test coverage insights** | Map test files to production types and highlight untested code |
| 🔜 | **Onboarding guides** | Suggested reading order derived from the dependency graph |
| 🔜 | **Diff comparison mode** | Side-by-side architecture comparison of two branches |
| 🔜 | **Export capabilities** | Export graphs and overviews as SVG, PNG, PDF, or CSV |
| 📋 | **WebSocket streaming** | Replace polling with real-time progress via WebSockets |
| 📋 | **Repository comparison** | Compare architecture/stats of two different repositories |
| 📋 | **CI integration** | GitHub Action that runs RepoLens analysis on PRs |
| 📋 | **Rate limiting + queue** | Multi-tenant deployment with request queuing |

---

## License

This project is for anyone to use so long as it is not used to achieve access that you do not have.
