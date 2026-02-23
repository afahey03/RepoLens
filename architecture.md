# RepoLens Architecture

RepoLens follows a modular architecture designed for clarity and future extensibility.

---

# High-Level Flow

User → API → Download → Analysis (scan + parse + graph + overview) → Search Index → API → Frontend

---

# Core Components

## RepoLens.Api

ASP.NET Core Web API on port 5000. CORS enabled for the Vite dev server (port 5173).

Responsibilities:

* Accept repository URL via POST /api/repository/analyze
* Trigger download and full analysis pipeline (AnalyzeFullAsync)
* Cache results in-memory per repository ID
* Serve overview, architecture graph, and search results

Endpoints:

* POST /api/repository/analyze — download + analyze a public GitHub repo
* GET /api/repository/{id}/overview — repository overview (languages, frameworks, entry points)
* GET /api/repository/{id}/architecture — dependency graph (nodes + edges)
* GET /api/repository/{id}/architecture/stats — graph aggregate stats (node/edge type counts, depth, root/leaf nodes)
* GET /api/repository/{id}/search?q=&kinds=&skip=&take= — BM25-ranked symbol/file search with kind filtering and pagination
* GET /api/repository/{id}/suggest?q= — autocomplete suggestions for symbols and files
* POST /api/repository/{id}/pr-impact — analyze impact of a pull request (changed files, affected symbols, downstream dependencies)

Error handling returns 400, 422, or 500 with descriptive messages.

---

## RepoLens.Analysis

Core analysis library. Contains:

### GitHubPrDiffFetcher (IPrDiffFetcher)

* Fetches file-level PR diff from GitHub REST API v3 (`/repos/{owner}/{repo}/pulls/{number}/files`)
* Supports pagination for PRs with many changed files
* Returns file path, status (added/removed/modified/renamed), additions, deletions, previous path (renames), and patch text
* Supports GitHub PAT for private repos and higher rate limits

### PrImpactAnalyzer

* Cross-references PR diff against cached repository analysis
* Identifies affected symbols (classes, methods, properties, etc.) defined in changed files
* Identifies affected graph edges (imports, inheritance, containment) connected to changed files/symbols
* Computes downstream dependencies — files that import changed files (ripple effect)
* Reports languages touched, total additions/deletions, and per-file symbol counts

### GitHubRepositoryDownloader

* Downloads zip archives from GitHub (tries main, then master branch)
* Caches downloads in %TEMP%/RepoLens/{owner}_{repo}
* ClearLocalCache removes the cached directory so re-analysis fetches fresh content
* Validates GitHub URLs, streams downloads, cleans up on failure

### OpenAiSummaryEnricher (ISummaryEnricher)

* Generates rich natural-language summaries via OpenAI Chat Completions API
* Compatible with any OpenAI-compatible endpoint (OpenAI, Azure OpenAI, Ollama, LM Studio)
* Builds a structured prompt from: repo metadata, language/framework/symbol data, key types, external deps, and README excerpt (up to 3K chars)
* Uses gpt-4o-mini by default (configurable via `REPOLENS_OPENAI_MODEL` env var)
* API key: per-request `openAiApiKey` field OR server-side `REPOLENS_OPENAI_API_KEY` env var
* Base URL configurable via `REPOLENS_OPENAI_BASE_URL` (defaults to `https://api.openai.com/v1`)
* Falls back gracefully to template summary when no key is configured or if the call fails
* Called after analysis + indexing, before caching — enriches the overview's Summary field

### RepositoryAnalyzer

* Orchestrates the full analysis pipeline via AnalyzeFullAsync
* Scans files once and reuses the file list for graph, overview, and search
* **Incremental re-analysis via AnalyzeIncrementalAsync**:
  * Computes SHA-256 content hashes during file scanning
  * Compares current hashes against previous cached hashes to identify changed/new/removed files
  * Reuses cached symbols for unchanged files; re-extracts only for files with different hashes
  * Merges cached and fresh symbols, then rebuilds graph and overview from merged data
  * If no files changed, returns cached analysis immediately (fast path)
* File scanning recognizes 30+ extensions across C#, TypeScript, JavaScript, Python, Go, Java, Rust, Ruby, PHP, and more
* Skips binary files, hidden directories, and build output
* Detects frameworks: .NET, ASP.NET Core, Node.js, TypeScript, React, Vue, Angular, Docker
* Identifies entry points (Program.cs, index.ts, main.py, etc.)
* Reads README.md (if present, up to 10 KB) for LLM summary enrichment
* Builds folder hierarchy as graph nodes with containment edges
* **Overview summarization** (Phase 4):
  * Language-by-lines breakdown (not just file counts)
  * Symbol counts aggregated by kind (Class, Method, Function, Interface, etc.)
  * Key types ranked by member count (top 10 classes/interfaces with most methods/properties)
  * Most connected modules ranked by import edge traffic (incoming + outgoing)
  * External dependency detection from package.json (npm) and .csproj (NuGet PackageReference)
  * Automatic complexity classification (Tiny/Small/Medium/Large/Huge) using composite scoring
  * Auto-generated plain-English summary describing repo size, languages, frameworks, key types, entry points, and symbol density

### Language Parsers (ILanguageParser)

Each parser extracts symbols and builds dependency graph nodes/edges.

**RoslynCSharpParser** — AST-level parsing via Microsoft.CodeAnalysis (Roslyn):

* Uses `CSharpSyntaxTree.ParseText` and a `CSharpSyntaxWalker` for full syntax-tree walking
* Extracts: namespaces (block + file-scoped), classes, interfaces, records, structs, enums, methods, constructors, properties, using directives
* Graph nodes: Namespace nodes, Class/Interface nodes
* Graph edges: File → Imports → Namespace, Namespace → Contains → Class, Class → Inherits/Implements → Base type (from `BaseListSyntax`), File → Contains → Type
* Handles nested types, generic constraints, primary constructors, and multi-line declarations correctly
* Filters import edges to only reference repo-internal namespaces
* Caches parse results to avoid double work
* Replaces the earlier regex-based CSharpParser (retained but no longer wired)

**JavaScriptTypeScriptParser** — regex-based analysis of .ts/.tsx/.js/.jsx/.mjs/.cjs files:

* Extracts: import/require statements, classes, functions (declarations + arrows), interfaces (TS), type aliases (TS), enums (TS)
* Graph nodes: Module nodes (one per file), Class/Function/Interface nodes
* Graph edges: Module → Imports → Module (with relative path resolution), Module → Contains → Class/Function, Class → Inherits/Implements
* Resolves relative import paths to actual files (tries .ts, .tsx, .js, .jsx, index.* variants)
* External npm package imports are excluded from graph edges

**PythonParser** — regex-based analysis of .py/.pyw files:

* Extracts: `import`/`from...import` statements, classes (with base classes), functions/methods, decorators (`@`), top-level constants (ALL_CAPS)
* Graph nodes: Module (per file), Class, Function nodes
* Graph edges: Module → Imports → Module (relative `.` imports resolved), Module → Contains → Class/Function, Class → Inherits → Base
* Resolves Python imports via dotted module paths (package.submodule → path/submodule.py or package/__init__.py)
* Tracks class scope via indentation to distinguish methods from top-level functions

**JavaParser** — regex-based analysis of .java files:

* Extracts: package declarations, imports (including static), classes (extends/implements), interfaces (extends), enums, annotations (`@`), methods
* Graph nodes: Namespace (package), Module (file), Class, Interface nodes
* Graph edges: Namespace → Contains → Module, Module → Contains → Class, Class → Inherits/Implements, Module → Imports → Module
* Brace-depth tracking to determine class scope for method extraction
* Resolves imports via package→path mapping (com.example.Foo → com/example/Foo.java)

**GoParser** — regex-based analysis of .go files:

* Extracts: package declarations, single and grouped imports, structs, interfaces, functions, receiver methods, type aliases, const/var declarations
* Graph nodes: Module (per file), Class (struct), Interface, Function nodes
* Graph edges: Module → Contains → Struct/Interface/Function, Module → Imports → Module
* Reads `go.mod` to detect the module path for resolving internal imports
* Receiver methods stored with ParentSymbol set to the receiver type name

All parsers skip files over 1 MB (likely generated) and ignore standard directories (node_modules, bin, obj, .git, vendor, __pycache__, target, etc.).

---

## RepoLens.Engine

### SearchEngine + SearchIndex

* In-memory inverted index built per repository
* Indexes both symbol names and file paths
* BM25 scoring (K1=1.2, B=0.75) with 2x boost for symbol name matches
* Tokenization: splits on non-alphanumeric characters + camelCase boundary splitting
* Supports querying by symbol name, file name, or partial matches
* **Kind filtering** (Phase 6): filter results by symbol kind (Class, Function, File, etc.)
* **Pagination** (Phase 6): skip/take parameters with total result count
* **Autocomplete suggestions** (Phase 6): prefix + substring matching returning top symbols/files
* **Kind facets** (Phase 6): returns all available symbol kinds per query for filter UI

---

## RepoLens.Shared

Shared models and contracts used across all projects:

### Models

* **GraphNode** — Id, Name, Type (Repository/Folder/File/Namespace/Class/Interface/Function/Module), FilePath, Metadata dict
* **GraphEdge** — Source, Target, Relationship (Contains/Imports/Calls/Inherits/Implements)
* **DependencyGraph** — Lists of Nodes and Edges
* **SymbolInfo** — Name, Kind (Class/Interface/Method/Property/Function/Variable/Import/Namespace/Module), FilePath, Line, ParentSymbol
* **SearchResult** — FilePath, Symbol, Snippet, Score, Line
* **FileInfo** — RelativePath, Language, SizeBytes, LineCount, ContentHash (SHA-256)
* **RepositoryOverview** — Name, Url, LanguageBreakdown, LanguageLineBreakdown, TotalFiles, TotalLines, DetectedFrameworks, EntryPoints, TopLevelFolders, SymbolCounts, KeyTypes, MostConnectedModules, ExternalDependencies, Summary, SummarySource ("template" | "ai"), Complexity
* **KeyTypeInfo** — Name, FilePath, Kind, MemberCount (classes/interfaces ranked by member count)
* **ConnectedModuleInfo** — Name, FilePath, IncomingEdges, OutgoingEdges (modules with most import traffic)

### DTOs

* AnalyzeRequest / AnalyzeResponse
* ArchitectureResponse (GraphNodeDto, GraphEdgeDto)
* GraphStatsResponse (TotalNodes, TotalEdges, NodeTypeCounts, EdgeTypeCounts, MaxDepth, RootNodes, LeafNodes)
* SearchResponse (Query, TotalResults, Skip, Take, AvailableKinds, Results) — paginated with kind facets
* SuggestResponse (Prefix, Suggestions) / SearchSuggestionDto

* **PrImpactRequest** — PrNumber, GitHubToken (optional)
* **PrImpactResponse** — PrNumber, TotalFilesChanged, TotalAdditions, TotalDeletions, ChangedFiles, AffectedSymbols, AffectedEdges, DownstreamFiles, LanguagesTouched
* **PrFileImpact** — FilePath, Status, Additions, Deletions, Language, PreviousFilePath, SymbolCount
* **PrSymbolImpact** — Name, Kind, FilePath, Line, ParentSymbol
* **PrEdgeImpact** — Source, Target, Relationship, ImpactSide

### Contracts

* IRepositoryDownloader — DownloadAsync(url)
* IRepositoryAnalyzer — ScanFilesAsync, ExtractSymbolsAsync, BuildDependencyGraphAsync, GenerateOverviewAsync, AnalyzeFullAsync
* ISearchEngine — BuildIndex, Search
* IPrDiffFetcher — FetchDiffAsync(owner, repo, prNumber)

---

## RepoLens.Web

React 19 + Vite 6 + TypeScript 5.6 frontend.

Pages:

Home

* Repository URL input
* Triggers analysis via API

Explorer (tabbed)

* **Overview Panel** — summary banner with complexity badge, language-by-lines stacked bar chart, symbol counts table, key types list, most connected modules (with incoming/outgoing edge counts), external dependency tags, frameworks, entry points, folder structure
* **Architecture Graph** (Phase 5 enhanced) — interactive React Flow (@xyflow/react) DAG visualization with:
  * Dagre auto-layout (vertical/horizontal toggle)
  * Custom styled nodes with type icons, color-coded borders, and file path display
  * Edge styling by relationship type (color + dash pattern + animation for imports)
  * Filtering toolbar to show/hide node types and edge types
  * MiniMap for navigation in large graphs
  * Click-to-inspect node detail sidebar showing metadata, incoming/outgoing edges
  * Color-coded legend for all node and edge types
  * Graph stats bar showing depth and node type distribution
  * Folder and Contains edges hidden by default for cleaner views
* **Search Panel** (Phase 6 enhanced) — faceted search experience with:
  * Debounced search-as-you-type (300ms) with loading spinner
  * Autocomplete suggestions dropdown (prefix + substring matching with keyboard navigation)
  * Kind filter chips (toggle Class, Function, Interface, File, etc.)
  * Results grouped by file with collapsible headers
  * Kind-badge icons and color coding on each result
  * Query term highlighting in symbol names and snippets
  * Pagination controls (prev/next) for large result sets
  * Welcome state with example query buttons
* **PR Impact Panel** — analyze pull request impact against the repository's codebase:
  * PR number input with analyze button
  * Summary stats grid (files changed, additions, deletions, symbols affected, downstream files, edges affected)
  * Languages touched tags
  * Changed file list with status badges, line delta, and expandable symbol lists
  * Downstream dependency list — files that import changed files
  * Color-coded symbol kind badges within each file

---

# Design Principles

* Clear separation of concerns (download → scan → parse → graph → index → serve)
* Optional AI enrichment — LLM-powered summaries via OpenAI-compatible API, with graceful fallback
* Graph-first architecture — everything maps to nodes and edges
* Single-pass analysis — files scanned once, results reused everywhere
* Performance-aware — file size limits, directory filtering, cached parse results
* Clean, readable code with comprehensive logging

---

# Technology Stack

* **Backend**: .NET 8, ASP.NET Core Web API, C#
* **Frontend**: React 19, Vite 6, TypeScript 5.6, @xyflow/react 12
* **Search**: BM25 in-memory inverted index
* **Parsing**: Roslyn AST for C# (Microsoft.CodeAnalysis.CSharp 4.12), regex for JS/TS/Python/Java/Go
* **Solution format**: .slnx (newer .NET solution format)

---

# Future Extensions

* VS Code extension
* Rust, Ruby, PHP parser implementations
* Export capabilities (SVG, PNG, PDF, CSV)
* WebSocket streaming (replace polling)
* Repository comparison
* CI integration (GitHub Action for PRs)
