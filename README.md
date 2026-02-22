# RepoLens

**Understand any GitHub repository in seconds.**

RepoLens analyzes public GitHub repositories and generates an interactive overview, architecture graph, and intelligent code search — all from a single URL.

---

## Features

- **Repository Overview** — language breakdown, frameworks detected, entry points, key types, external dependencies, and a generated plain-English summary
- **Interactive Architecture Graph** — dagre-layouted dependency graph with zoom, pan, and node details powered by React Flow
- **BM25 Code Search** — full-text search across all symbols with kind filtering, pagination, autocomplete suggestions, and debounced input
- **Multi-Language Parsing** — C#, TypeScript, JavaScript, Python, Java, and Go
- **Persistent Disk Cache** — analyzed repos survive restarts; backed by JSON on disk
- **Download Resilience** — exponential-backoff retries for GitHub downloads
- **Progressive Loading** — real-time progress bar with stage labels during analysis
- **Docker Support** — single-command production deployment via Docker Compose

---

## Pictures

<img width="1011" height="807" alt="image" src="https://github.com/user-attachments/assets/37e43c6a-5b2b-4da5-a4bf-1ef5fbd9dfff" />

<img width="996" height="905" alt="image" src="https://github.com/user-attachments/assets/5a7a3f65-0533-42fb-915f-b84ac090fbf5" />

<img width="1074" height="823" alt="image" src="https://github.com/user-attachments/assets/49c5a94a-62b1-43b1-9957-a70300718923" />

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend | .NET 8 / ASP.NET Core Web API |
| Frontend | React 19, Vite 6, TypeScript 5.6 |
| Graph | @xyflow/react 12, @dagrejs/dagre |
| Search | Custom BM25 inverted index |
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

---

## Project Structure

```
src/
├── RepoLens.Api/          # ASP.NET Core Web API (controllers, entry point)
├── RepoLens.Analysis/     # Repository downloading, file scanning, language parsers
├── RepoLens.Engine/       # Search engine, disk cache, dependency graph builder
├── RepoLens.Shared/       # Contracts, DTOs, models shared across projects
├── RepoLens.Web/          # React + Vite frontend
└── RepoLens.slnx          # .NET solution file
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
| C# | Regex-based | Classes, interfaces, methods, properties, enums, records |
| TypeScript | Regex-based | Classes, functions, interfaces, type aliases, enums |
| JavaScript | Regex-based | Classes, functions (including arrow/exported) |
| Python | Regex-based | Classes, functions, decorators |
| Java | Regex-based | Classes, interfaces, methods, enums |
| Go | Regex-based | Structs, interfaces, functions, methods |

---

## License

This project is for anyone to use so long as it is not used to achieve access that you do not have.
