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
        [".php"] = "PHP",
        [".sql"] = "SQL",
        [".sh"] = "Shell",
        [".ps1"] = "PowerShell",
        [".dockerfile"] = "Dockerfile",
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

        // 4. Generate overview reusing scanned files
        var overview = BuildOverviewFromFiles(repoPath, repositoryUrl, files);
        _logger.LogInformation("Overview complete: {Name} — {Files} files, {Lines} lines",
            overview.Name, overview.TotalFiles, overview.TotalLines);

        return new FullAnalysisResult
        {
            Files = files,
            Symbols = symbols,
            Graph = graph,
            Overview = overview
        };
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
    /// Builds the overview from an already-scanned file list (avoids re-scanning).
    /// </summary>
    private RepositoryOverview BuildOverviewFromFiles(string repoPath, string repositoryUrl, List<FileInfo> files)
    {
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
        if (repoName.EndsWith("-main") || repoName.EndsWith("-master"))
        {
            repoName = repoName[..repoName.LastIndexOf('-')];
        }

        return new RepositoryOverview
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

                // Count lines only for reasonably sized files
                var lineCount = 0;
                if (fileSize > 0 && fileSize <= MaxFileSizeBytes)
                {
                    try
                    {
                        lineCount = CountLines(filePath);
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
                    LineCount = lineCount
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
}
