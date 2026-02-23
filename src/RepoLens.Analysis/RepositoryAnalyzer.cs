using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Contracts;
using RepoLens.Shared.Models;
using FileInfo = RepoLens.Shared.Models.FileInfo;

namespace RepoLens.Analysis;

/// <summary>
/// Analyzes a repository: scans files, extracts symbols, builds dependency graph.
/// Delegates language-specific parsing to <see cref="ILanguageParser"/> implementations.
/// </summary>
public class RepositoryAnalyzer : IRepositoryAnalyzer
{
    private readonly IEnumerable<ILanguageParser> _parsers;
    private readonly ILogger<RepositoryAnalyzer> _logger;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".next", "coverage", "__pycache__",
        ".github", ".vscode", ".nuget", "wwwroot"
    };

    private static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", ".gitignore", ".gitattributes",
        "LICENSE", "LICENSE.md", "LICENSE.txt"
    };

    /// <summary>
    /// Maps file extensions to language names for supported code/config files.
    /// </summary>
    private static readonly Dictionary<string, string> ExtensionToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        // C# / .NET
        [".cs"] = "C#",
        [".csproj"] = "MSBuild",
        [".sln"] = "Solution",
        [".slnx"] = "Solution",
        [".props"] = "MSBuild",
        [".targets"] = "MSBuild",
        [".razor"] = "Razor",

        // JavaScript / TypeScript
        [".ts"] = "TypeScript",
        [".tsx"] = "TypeScript",
        [".js"] = "JavaScript",
        [".jsx"] = "JavaScript",
        [".mjs"] = "JavaScript",
        [".cjs"] = "JavaScript",

        // Web
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".scss"] = "SCSS",
        [".less"] = "LESS",

        // Data / Config
        [".json"] = "JSON",
        [".xml"] = "XML",
        [".yaml"] = "YAML",
        [".yml"] = "YAML",
        [".toml"] = "TOML",
        [".env"] = "Environment",

        // Documentation
        [".md"] = "Markdown",
        [".txt"] = "Text",

        // Other languages (future support)
        [".py"] = "Python",
        [".go"] = "Go",
        [".java"] = "Java",
        [".rs"] = "Rust",
        [".rb"] = "Ruby",
        [".rake"] = "Ruby",
        [".gemspec"] = "Ruby",
        [".php"] = "PHP",
        [".sql"] = "SQL",
        [".sh"] = "Shell",
        [".ps1"] = "PowerShell",
        [".dockerfile"] = "Dockerfile",

        // C / C++
        [".c"] = "C",
        [".h"] = "C",
        [".cpp"] = "C++",
        [".cxx"] = "C++",
        [".cc"] = "C++",
        [".hpp"] = "C++",
        [".hxx"] = "C++",
        [".hh"] = "C++",

        // Swift
        [".swift"] = "Swift",

        // Scala
        [".scala"] = "Scala",
        [".sc"] = "Scala",

        // Kotlin
        [".kt"] = "Kotlin",
        [".kts"] = "Kotlin",

        // Dart
        [".dart"] = "Dart",

        // Lua
        [".lua"] = "Lua",

        // Perl
        [".pl"] = "Perl",
        [".pm"] = "Perl",
        [".t"] = "Perl",

        // R
        [".r"] = "R",
        [".R"] = "R",
        [".Rmd"] = "R",

        // Haskell
        [".hs"] = "Haskell",
        [".lhs"] = "Haskell",

        // Elixir
        [".ex"] = "Elixir",
        [".exs"] = "Elixir",
    };

    /// <summary>
    /// Max file size to read for line counting (5 MB). Larger files are counted as 0 lines.
    /// </summary>
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;

    public RepositoryAnalyzer(IEnumerable<ILanguageParser> parsers, ILogger<RepositoryAnalyzer> logger)
    {
        _parsers = parsers;
        _logger = logger;
    }

    public Task<List<FileInfo>> ScanFilesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Scanning files in {RepoPath}...", repoPath);
        var files = new List<FileInfo>();
        ScanDirectory(repoPath, repoPath, files);
        _logger.LogInformation("Scan complete: {FileCount} files found across {LanguageCount} languages",
            files.Count, files.Select(f => f.Language).Distinct().Count());
        return Task.FromResult(files);
    }

    public async Task<List<SymbolInfo>> ExtractSymbolsAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Extracting symbols from {RepoPath}...", repoPath);
        var allSymbols = new List<SymbolInfo>();

        foreach (var parser in _parsers)
        {
            var symbols = await parser.ExtractSymbolsAsync(repoPath, cancellationToken);
            allSymbols.AddRange(symbols);
            _logger.LogDebug("Parser {Parser} extracted {Count} symbols",
                parser.GetType().Name, symbols.Count);
        }

        _logger.LogInformation("Symbol extraction complete: {Count} total symbols", allSymbols.Count);
        return allSymbols;
    }

    public async Task<DependencyGraph> BuildDependencyGraphAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Building dependency graph for {RepoPath}...", repoPath);
        var graph = new DependencyGraph();
        var files = await ScanFilesAsync(repoPath, cancellationToken);

        // Build folder hierarchy nodes
        var folderNodes = new HashSet<string>();
        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file.RelativePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && folderNodes.Add(dir))
            {
                graph.Nodes.Add(new GraphNode
                {
                    Id = $"folder:{dir}",
                    Name = dir.Split('/').Last(),
                    Type = NodeType.Folder,
                    FilePath = dir
                });
            }
        }

        // Add file nodes
        foreach (var file in files)
        {
            var fileNode = new GraphNode
            {
                Id = file.RelativePath,
                Name = Path.GetFileName(file.RelativePath),
                Type = NodeType.File,
                FilePath = file.RelativePath,
                Metadata = new Dictionary<string, string>
                {
                    ["language"] = file.Language,
                    ["lines"] = file.LineCount.ToString(),
                    ["size"] = file.SizeBytes.ToString()
                }
            };
            graph.Nodes.Add(fileNode);

            // Add "contains" edge from folder to file
            var dir = Path.GetDirectoryName(file.RelativePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir))
            {
                graph.Edges.Add(new GraphEdge
                {
                    Source = $"folder:{dir}",
                    Target = file.RelativePath,
                    Relationship = EdgeRelationship.Contains
                });
            }
        }

        // Add symbol nodes and edges from language parsers
        foreach (var parser in _parsers)
        {
            var (nodes, edges) = await parser.BuildDependenciesAsync(repoPath, cancellationToken);
            graph.Nodes.AddRange(nodes);
            graph.Edges.AddRange(edges);
        }

        _logger.LogInformation("Dependency graph built: {Nodes} nodes, {Edges} edges",
            graph.Nodes.Count, graph.Edges.Count);
        return graph;
    }

    public async Task<RepositoryOverview> GenerateOverviewAsync(string repoPath, string repositoryUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating overview for {RepoPath}...", repoPath);
        var files = await ScanFilesAsync(repoPath, cancellationToken);

        var languageBreakdown = files
            .Where(f => IsCodeLanguage(f.Language))
            .GroupBy(f => f.Language)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        var topLevelFolders = Directory.GetDirectories(repoPath)
            .Select(d => Path.GetFileName(d))
            .Where(name => !IgnoredDirectories.Contains(name))
            .OrderBy(name => name)
            .ToList();

        var repoName = Path.GetFileName(repoPath);
        // GitHub extracted folder names look like "repo-main", trim the branch suffix
        if (repoName.EndsWith("-main") || repoName.EndsWith("-master"))
        {
            repoName = repoName[..repoName.LastIndexOf('-')];
        }

        var overview = new RepositoryOverview
        {
            Name = repoName,
            Url = repositoryUrl,
            LanguageBreakdown = languageBreakdown,
            TotalFiles = files.Count,
            TotalLines = files.Sum(f => f.LineCount),
            TopLevelFolders = topLevelFolders,
            EntryPoints = DetectEntryPoints(files),
            DetectedFrameworks = DetectFrameworks(repoPath, files)
        };

        _logger.LogInformation("Overview complete: {Name} — {Files} files, {Lines} lines, {Langs} languages",
            overview.Name, overview.TotalFiles, overview.TotalLines, overview.LanguageBreakdown.Count);
        return overview;
    }

    public async Task<FullAnalysisResult> AnalyzeFullAsync(string repoPath, string repositoryUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Running full analysis for {RepoPath}...", repoPath);

        // 1. Scan files ONCE
        var files = await ScanFilesAsync(repoPath, cancellationToken);
        _logger.LogInformation("Scanned {Count} files", files.Count);

        // 2. Extract symbols
        var symbols = await ExtractSymbolsAsync(repoPath, cancellationToken);
        _logger.LogInformation("Extracted {Count} symbols", symbols.Count);

        // 3. Build dependency graph reusing scanned files
        var graph = BuildDependencyGraphFromFiles(repoPath, files);
        foreach (var parser in _parsers)
        {
            var (nodes, edges) = await parser.BuildDependenciesAsync(repoPath, cancellationToken);
            graph.Nodes.AddRange(nodes);
            graph.Edges.AddRange(edges);
        }
        _logger.LogInformation("Dependency graph built: {Nodes} nodes, {Edges} edges",
            graph.Nodes.Count, graph.Edges.Count);

        // 4. Generate overview reusing scanned files, symbols, and graph
        var overview = BuildOverviewFromFiles(repoPath, repositoryUrl, files, symbols, graph);
        _logger.LogInformation("Overview complete: {Name} — {Files} files, {Lines} lines, {Summary}",
            overview.Name, overview.TotalFiles, overview.TotalLines, overview.Complexity);

        // 5. Read README for LLM enrichment
        var readmeContent = ReadReadme(repoPath);

        return new FullAnalysisResult
        {
            Files = files,
            Symbols = symbols,
            Graph = graph,
            Overview = overview,
            ReadmeContent = readmeContent
        };
    }

    public async Task<FullAnalysisResult> AnalyzeIncrementalAsync(
        string repoPath, string repositoryUrl,
        CachedAnalysis previousAnalysis,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Running incremental analysis for {RepoPath}...", repoPath);

        // 1. Scan current files (also computes fresh hashes)
        var currentFiles = await ScanFilesAsync(repoPath, cancellationToken);
        _logger.LogInformation("Scanned {Count} files for incremental comparison", currentFiles.Count);

        // 2. Determine which files changed, were added, or removed
        var previousHashes = previousAnalysis.FileHashes;
        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unchangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in currentFiles)
        {
            if (previousHashes.TryGetValue(file.RelativePath, out var oldHash)
                && string.Equals(oldHash, file.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                unchangedPaths.Add(file.RelativePath);
            }
            else
            {
                changedPaths.Add(file.RelativePath);
            }
        }

        // Files that existed before but are now gone
        var removedPaths = previousHashes.Keys
            .Where(p => !currentFiles.Any(f => string.Equals(f.RelativePath, p, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Incremental diff: {Changed} changed/new, {Unchanged} unchanged, {Removed} removed",
            changedPaths.Count, unchangedPaths.Count, removedPaths.Count);

        // 3. If nothing changed, return the previous analysis directly
        if (changedPaths.Count == 0 && removedPaths.Count == 0)
        {
            _logger.LogInformation("No changes detected — returning cached analysis");
            return new FullAnalysisResult
            {
                Files = currentFiles,
                Symbols = previousAnalysis.Symbols,
                Graph = previousAnalysis.Graph,
                Overview = previousAnalysis.Overview
            };
        }

        // 4. Re-extract symbols via parsers and keep only those from changed files.
        //    For unchanged files, reuse symbols from the previous cache.
        var freshSymbols = await ExtractSymbolsAsync(repoPath, cancellationToken);
        var symbolsFromChangedFiles = freshSymbols
            .Where(s => changedPaths.Contains(s.FilePath))
            .ToList();

        var symbolsFromUnchangedFiles = previousAnalysis.Symbols
            .Where(s => unchangedPaths.Contains(s.FilePath))
            .ToList();

        var mergedSymbols = symbolsFromUnchangedFiles
            .Concat(symbolsFromChangedFiles)
            .ToList();

        _logger.LogInformation(
            "Symbols merged: {Reused} reused + {Fresh} fresh = {Total} total",
            symbolsFromUnchangedFiles.Count, symbolsFromChangedFiles.Count, mergedSymbols.Count);

        // 5. Rebuild dependency graph (structural, fast)
        var graph = BuildDependencyGraphFromFiles(repoPath, currentFiles);
        foreach (var parser in _parsers)
        {
            var (nodes, edges) = await parser.BuildDependenciesAsync(repoPath, cancellationToken);
            graph.Nodes.AddRange(nodes);
            graph.Edges.AddRange(edges);
        }

        // 6. Rebuild overview from merged data
        var overview = BuildOverviewFromFiles(repoPath, repositoryUrl, currentFiles, mergedSymbols, graph);

        _logger.LogInformation(
            "Incremental analysis complete: {Files} files, {Symbols} symbols, {Nodes} graph nodes",
            currentFiles.Count, mergedSymbols.Count, graph.Nodes.Count);

        var readmeContent = ReadReadme(repoPath);

        return new FullAnalysisResult
        {
            Files = currentFiles,
            Symbols = mergedSymbols,
            Graph = graph,
            Overview = overview,
            ReadmeContent = readmeContent
        };
    }

    /// <summary>
    /// Reads the repository's README file if present (README.md, README.txt, README).
    /// Returns null if not found. Caps at 10 KB.
    /// </summary>
    private static string? ReadReadme(string repoPath)
    {
        var candidates = new[] { "README.md", "readme.md", "README.MD", "README.txt", "README", "Readme.md" };
        foreach (var name in candidates)
        {
            var path = Path.Combine(repoPath, name);
            if (File.Exists(path))
            {
                var info = new System.IO.FileInfo(path);
                if (info.Length > 10 * 1024) // Cap at 10 KB
                    return File.ReadAllText(path)[..(10 * 1024)];
                return File.ReadAllText(path);
            }
        }
        return null;
    }

    /// <summary>
    /// Builds the dependency graph from an already-scanned file list (avoids re-scanning).
    /// </summary>
    private DependencyGraph BuildDependencyGraphFromFiles(string repoPath, List<FileInfo> files)
    {
        var graph = new DependencyGraph();
        var folderNodes = new HashSet<string>();

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file.RelativePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && folderNodes.Add(dir))
            {
                graph.Nodes.Add(new GraphNode
                {
                    Id = $"folder:{dir}",
                    Name = dir.Split('/').Last(),
                    Type = NodeType.Folder,
                    FilePath = dir
                });
            }
        }

        foreach (var file in files)
        {
            var fileNode = new GraphNode
            {
                Id = file.RelativePath,
                Name = Path.GetFileName(file.RelativePath),
                Type = NodeType.File,
                FilePath = file.RelativePath,
                Metadata = new Dictionary<string, string>
                {
                    ["language"] = file.Language,
                    ["lines"] = file.LineCount.ToString(),
                    ["size"] = file.SizeBytes.ToString()
                }
            };
            graph.Nodes.Add(fileNode);

            var dir = Path.GetDirectoryName(file.RelativePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir))
            {
                graph.Edges.Add(new GraphEdge
                {
                    Source = $"folder:{dir}",
                    Target = file.RelativePath,
                    Relationship = EdgeRelationship.Contains
                });
            }
        }

        return graph;
    }

    /// <summary>
    /// Builds the overview from pre-computed files, symbols, and graph.
    /// </summary>
    private RepositoryOverview BuildOverviewFromFiles(
        string repoPath, string repositoryUrl,
        List<FileInfo> files, List<SymbolInfo> symbols, DependencyGraph graph)
    {
        // ── Language breakdown by file count and by line count ──
        var codeFiles = files.Where(f => IsCodeLanguage(f.Language)).ToList();

        var languageBreakdown = codeFiles
            .GroupBy(f => f.Language)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        var languageLineBreakdown = codeFiles
            .GroupBy(f => f.Language)
            .OrderByDescending(g => g.Sum(f => f.LineCount))
            .ToDictionary(g => g.Key, g => g.Sum(f => f.LineCount));

        // ── Basic info ──
        var topLevelFolders = Directory.GetDirectories(repoPath)
            .Select(d => Path.GetFileName(d))
            .Where(name => !IgnoredDirectories.Contains(name))
            .OrderBy(name => name)
            .ToList();

        var repoName = Path.GetFileName(repoPath);
        if (repoName.EndsWith("-main") || repoName.EndsWith("-master"))
            repoName = repoName[..repoName.LastIndexOf('-')];

        var totalLines = files.Sum(f => f.LineCount);
        var frameworks = DetectFrameworks(repoPath, files);
        var entryPoints = DetectEntryPoints(files);

        // ── Symbol counts by kind ──
        var symbolCounts = symbols
            .Where(s => s.Kind != SymbolKind.Import)
            .GroupBy(s => s.Kind.ToString())
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        // ── Key types (classes/interfaces with most members) ──
        var typeSymbols = symbols
            .Where(s => s.Kind is SymbolKind.Class or SymbolKind.Interface)
            .ToHashSet();

        var typeNames = typeSymbols
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());

        var membersByType = symbols
            .Where(s => s.ParentSymbol is not null
                && s.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Function)
            .GroupBy(s => s.ParentSymbol!)
            .ToDictionary(g => g.Key, g => g.Count());

        var keyTypes = membersByType
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv =>
            {
                typeNames.TryGetValue(kv.Key, out var sym);
                return new KeyTypeInfo
                {
                    Name = kv.Key,
                    FilePath = sym?.FilePath ?? "",
                    Kind = sym?.Kind.ToString() ?? "Class",
                    MemberCount = kv.Value
                };
            })
            .ToList();

        // ── Most connected modules (by import edges) ──
        var importEdges = graph.Edges
            .Where(e => e.Relationship == EdgeRelationship.Imports)
            .ToList();

        var outgoing = importEdges
            .GroupBy(e => e.Source)
            .ToDictionary(g => g.Key, g => g.Count());

        var incoming = importEdges
            .GroupBy(e => e.Target)
            .ToDictionary(g => g.Key, g => g.Count());

        var allModuleIds = outgoing.Keys.Union(incoming.Keys).ToHashSet();
        var nodeById = graph.Nodes
            .GroupBy(n => n.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var mostConnected = allModuleIds
            .Select(id =>
            {
                outgoing.TryGetValue(id, out var outCount);
                incoming.TryGetValue(id, out var inCount);
                nodeById.TryGetValue(id, out var node);
                return new ConnectedModuleInfo
                {
                    Name = node?.Name ?? id,
                    FilePath = node?.FilePath ?? "",
                    IncomingEdges = inCount,
                    OutgoingEdges = outCount
                };
            })
            .OrderByDescending(m => m.IncomingEdges + m.OutgoingEdges)
            .Take(10)
            .ToList();

        // ── External dependencies ──
        var externalDeps = DetectExternalDependencies(repoPath, files);

        // ── Complexity classification ──
        var complexity = ClassifyComplexity(totalLines, files.Count, symbols.Count);

        // ── Auto-generated summary ──
        var summary = GenerateSummary(
            repoName, languageBreakdown, frameworks, totalLines,
            files.Count, symbols.Count, keyTypes, entryPoints, complexity);

        return new RepositoryOverview
        {
            Name = repoName,
            Url = repositoryUrl,
            LanguageBreakdown = languageBreakdown,
            LanguageLineBreakdown = languageLineBreakdown,
            TotalFiles = files.Count,
            TotalLines = totalLines,
            TopLevelFolders = topLevelFolders,
            EntryPoints = entryPoints,
            DetectedFrameworks = frameworks,
            SymbolCounts = symbolCounts,
            KeyTypes = keyTypes,
            MostConnectedModules = mostConnected,
            ExternalDependencies = externalDeps,
            Summary = summary,
            Complexity = complexity
        };
    }

    private void ScanDirectory(string rootPath, string currentPath, List<FileInfo> files)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                var dirName = Path.GetFileName(dir);
                if (IgnoredDirectories.Contains(dirName) || dirName.StartsWith('.'))
                    continue;

                ScanDirectory(rootPath, dir, files);
            }

            foreach (var filePath in Directory.GetFiles(currentPath))
            {
                var fileName = Path.GetFileName(filePath);

                // Skip hidden and ignored files
                if (fileName.StartsWith('.') || IgnoredFiles.Contains(fileName))
                    continue;

                var ext = Path.GetExtension(filePath);

                // Handle Dockerfile (no extension)
                string? language = null;
                if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
                {
                    language = "Dockerfile";
                }
                else if (!ExtensionToLanguage.TryGetValue(ext, out language))
                {
                    continue;
                }

                var fileSize = new System.IO.FileInfo(filePath).Length;
                var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

                // Count lines and compute content hash for reasonably sized files
                var lineCount = 0;
                string? contentHash = null;
                if (fileSize > 0 && fileSize <= MaxFileSizeBytes)
                {
                    try
                    {
                        lineCount = CountLines(filePath);
                        contentHash = ComputeFileHash(filePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not read {File}: {Error}", relativePath, ex.Message);
                    }
                }

                files.Add(new FileInfo
                {
                    RelativePath = relativePath,
                    Language = language,
                    SizeBytes = fileSize,
                    LineCount = lineCount,
                    ContentHash = contentHash
                });
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Access denied scanning {Path}: {Error}", currentPath, ex.Message);
        }
    }

    /// <summary>
    /// Efficiently counts lines in a file using a buffered stream reader.
    /// </summary>
    private static int CountLines(string filePath)
    {
        var count = 0;
        using var reader = new StreamReader(filePath);
        while (reader.ReadLine() is not null)
            count++;
        return count;
    }

    /// <summary>
    /// Computes a SHA-256 hash of a file's contents, returned as a lowercase hex string.
    /// </summary>
    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Returns true for language names that represent actual code (not config/docs).
    /// </summary>
    private static bool IsCodeLanguage(string language) =>
        language is "C#" or "TypeScript" or "JavaScript" or "Python" or "Go"
            or "Java" or "Rust" or "Ruby" or "PHP" or "Razor" or "SQL"
            or "Shell" or "PowerShell";

    private static List<string> DetectEntryPoints(List<FileInfo> files)
    {
        var entryPatterns = new[]
        {
            "Program.cs", "Startup.cs",
            "index.ts", "index.js", "index.tsx", "index.jsx",
            "main.ts", "main.js", "main.tsx", "main.jsx",
            "App.tsx", "App.jsx", "App.ts", "App.js",
            "app.py", "main.py", "manage.py",
            "main.go", "Main.java"
        };
        return files
            .Where(f => entryPatterns.Any(p => f.RelativePath.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.RelativePath)
            .ToList();
    }

    private static List<string> DetectFrameworks(string repoPath, List<FileInfo> files)
    {
        var frameworks = new List<string>();

        // .NET
        if (files.Any(f => f.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            frameworks.Add(".NET");

        // Node.js
        var hasPackageJson = files.Any(f => f.RelativePath.EndsWith("package.json", StringComparison.OrdinalIgnoreCase));
        if (hasPackageJson)
            frameworks.Add("Node.js");

        // TypeScript
        if (File.Exists(Path.Combine(repoPath, "tsconfig.json")))
            frameworks.Add("TypeScript");

        // React (check package.json for react dependency)
        if (hasPackageJson)
        {
            try
            {
                var packageJsonPaths = files
                    .Where(f => f.RelativePath == "package.json")
                    .Select(f => Path.Combine(repoPath, f.RelativePath));

                foreach (var pkgPath in packageJsonPaths)
                {
                    var content = File.ReadAllText(pkgPath);
                    if (content.Contains("\"react\"", StringComparison.OrdinalIgnoreCase))
                    {
                        frameworks.Add("React");
                        break;
                    }
                    if (content.Contains("\"vue\"", StringComparison.OrdinalIgnoreCase))
                    {
                        frameworks.Add("Vue");
                        break;
                    }
                    if (content.Contains("\"@angular/core\"", StringComparison.OrdinalIgnoreCase))
                    {
                        frameworks.Add("Angular");
                        break;
                    }
                }
            }
            catch
            {
                // Ignore package.json read errors
            }
        }

        // ASP.NET (look for web-related csproj content)
        var csprojFiles = files
            .Where(f => f.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.Combine(repoPath, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        foreach (var csproj in csprojFiles)
        {
            try
            {
                var content = File.ReadAllText(csproj);
                if (content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                {
                    if (!frameworks.Contains("ASP.NET Core"))
                        frameworks.Add("ASP.NET Core");
                    break;
                }
            }
            catch
            {
                // Ignore read errors
            }
        }

        // Docker
        if (files.Any(f => Path.GetFileName(f.RelativePath).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)))
            frameworks.Add("Docker");

        return frameworks;
    }

    // ─── Phase 4: Summarization helpers ────────────────────────────────

    /// <summary>
    /// Detects external dependencies from package.json (npm) and .csproj (NuGet).
    /// </summary>
    private static List<string> DetectExternalDependencies(string repoPath, List<FileInfo> files)
    {
        var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // npm dependencies from root package.json
        var rootPackageJson = Path.Combine(repoPath, "package.json");
        if (File.Exists(rootPackageJson))
        {
            try
            {
                var content = File.ReadAllText(rootPackageJson);
                ExtractNpmDeps(content, deps);
            }
            catch { /* ignore */ }
        }

        // NuGet dependencies from .csproj files
        foreach (var file in files.Where(f => f.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var csprojPath = Path.Combine(repoPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var content = File.ReadAllText(csprojPath);
                ExtractNuGetDeps(content, deps);
            }
            catch { /* ignore */ }
        }

        return deps.OrderBy(d => d).ToList();
    }

    private static void ExtractNpmDeps(string packageJsonContent, HashSet<string> deps)
    {
        // Simple regex extraction of "dependencies" and "devDependencies" keys
        // Matches: "package-name": "version"
        var inDepsSection = false;
        foreach (var line in packageJsonContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("\"dependencies\"") || trimmed.Contains("\"devDependencies\"") ||
                trimmed.Contains("\"peerDependencies\""))
            {
                inDepsSection = true;
                continue;
            }

            if (inDepsSection)
            {
                if (trimmed.StartsWith('}'))
                {
                    inDepsSection = false;
                    continue;
                }

                // Extract package name from "name": "version"
                var quoteStart = trimmed.IndexOf('"');
                if (quoteStart >= 0)
                {
                    var quoteEnd = trimmed.IndexOf('"', quoteStart + 1);
                    if (quoteEnd > quoteStart + 1)
                    {
                        var name = trimmed[(quoteStart + 1)..quoteEnd];
                        if (!string.IsNullOrEmpty(name) && !name.StartsWith("//"))
                            deps.Add(name);
                    }
                }
            }
        }
    }

    private static void ExtractNuGetDeps(string csprojContent, HashSet<string> deps)
    {
        // Match <PackageReference Include="Name" .../>
        var regex = new System.Text.RegularExpressions.Regex(
            @"<PackageReference\s+Include=""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in regex.Matches(csprojContent))
        {
            deps.Add(match.Groups[1].Value);
        }
    }

    /// <summary>
    /// Classifies repository complexity based on lines, files, and symbols.
    /// </summary>
    private static string ClassifyComplexity(int totalLines, int totalFiles, int symbolCount)
    {
        // Use a composite score
        var score = totalLines + totalFiles * 10 + symbolCount * 5;

        return score switch
        {
            < 500 => "Tiny",
            < 5_000 => "Small",
            < 50_000 => "Medium",
            < 500_000 => "Large",
            _ => "Huge"
        };
    }

    /// <summary>
    /// Generates a plain-English summary of the repository.
    /// </summary>
    private static string GenerateSummary(
        string name,
        Dictionary<string, int> languageBreakdown,
        List<string> frameworks,
        int totalLines,
        int totalFiles,
        int symbolCount,
        List<KeyTypeInfo> keyTypes,
        List<string> entryPoints,
        string complexity)
    {
        var parts = new List<string>();

        // Opening sentence
        var primaryLang = languageBreakdown.Keys.FirstOrDefault() ?? "unknown";
        parts.Add($"{name} is a {complexity.ToLowerInvariant()}-sized {primaryLang} repository " +
                   $"with {totalFiles:N0} files and {totalLines:N0} lines of code.");

        // Frameworks
        if (frameworks.Count > 0)
        {
            var fwList = string.Join(", ", frameworks);
            parts.Add($"It uses {fwList}.");
        }

        // Language mix
        if (languageBreakdown.Count > 1)
        {
            var langs = string.Join(", ", languageBreakdown
                .Select(kv => $"{kv.Key} ({kv.Value} files)"));
            parts.Add($"Languages: {langs}.");
        }

        // Key types
        if (keyTypes.Count > 0)
        {
            var top = keyTypes.Take(3)
                .Select(t => $"{t.Name} ({t.MemberCount} members)");
            parts.Add($"Key types: {string.Join(", ", top)}.");
        }

        // Entry points
        if (entryPoints.Count > 0)
        {
            if (entryPoints.Count <= 3)
            {
                parts.Add($"Entry points: {string.Join(", ", entryPoints.Select(Path.GetFileName))}.");
            }
            else
            {
                parts.Add($"{entryPoints.Count} entry points detected.");
            }
        }

        // Symbol density
        if (symbolCount > 0 && totalFiles > 0)
        {
            var density = (double)symbolCount / totalFiles;
            parts.Add($"Symbol density: {density:F1} symbols per file ({symbolCount:N0} total).");
        }

        return string.Join(" ", parts);
    }
}
