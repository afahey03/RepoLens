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

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".next", "coverage", "__pycache__"
    };

    private static readonly Dictionary<string, string> ExtensionToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#",
        [".ts"] = "TypeScript",
        [".tsx"] = "TypeScript",
        [".js"] = "JavaScript",
        [".jsx"] = "JavaScript",
        [".json"] = "JSON",
        [".md"] = "Markdown",
        [".css"] = "CSS",
        [".html"] = "HTML",
        [".xml"] = "XML",
        [".yaml"] = "YAML",
        [".yml"] = "YAML",
        [".csproj"] = "MSBuild",
        [".sln"] = "Solution",
    };

    public RepositoryAnalyzer(IEnumerable<ILanguageParser> parsers)
    {
        _parsers = parsers;
    }

    public Task<List<FileInfo>> ScanFilesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var files = new List<FileInfo>();
        ScanDirectory(repoPath, repoPath, files);
        return Task.FromResult(files);
    }

    public async Task<List<SymbolInfo>> ExtractSymbolsAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var allSymbols = new List<SymbolInfo>();

        foreach (var parser in _parsers)
        {
            var symbols = await parser.ExtractSymbolsAsync(repoPath, cancellationToken);
            allSymbols.AddRange(symbols);
        }

        return allSymbols;
    }

    public async Task<DependencyGraph> BuildDependencyGraphAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var graph = new DependencyGraph();
        var files = await ScanFilesAsync(repoPath, cancellationToken);
        var symbols = await ExtractSymbolsAsync(repoPath, cancellationToken);

        // Add file nodes
        foreach (var file in files)
        {
            graph.Nodes.Add(new GraphNode
            {
                Id = file.RelativePath,
                Name = Path.GetFileName(file.RelativePath),
                Type = NodeType.File,
                FilePath = file.RelativePath,
                Metadata = new Dictionary<string, string>
                {
                    ["language"] = file.Language,
                    ["lines"] = file.LineCount.ToString()
                }
            });
        }

        // Add symbol nodes and edges from parsers
        foreach (var parser in _parsers)
        {
            var (nodes, edges) = await parser.BuildDependenciesAsync(repoPath, cancellationToken);
            graph.Nodes.AddRange(nodes);
            graph.Edges.AddRange(edges);
        }

        return graph;
    }

    public async Task<RepositoryOverview> GenerateOverviewAsync(string repoPath, string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var files = await ScanFilesAsync(repoPath, cancellationToken);

        var languageBreakdown = files
            .GroupBy(f => f.Language)
            .ToDictionary(g => g.Key, g => g.Count());

        var topLevelFolders = Directory.GetDirectories(repoPath)
            .Select(d => Path.GetFileName(d))
            .Where(name => !IgnoredDirectories.Contains(name))
            .OrderBy(name => name)
            .ToList();

        var overview = new RepositoryOverview
        {
            Name = Path.GetFileName(repoPath),
            Url = repositoryUrl,
            LanguageBreakdown = languageBreakdown,
            TotalFiles = files.Count,
            TotalLines = files.Sum(f => f.LineCount),
            TopLevelFolders = topLevelFolders,
            EntryPoints = DetectEntryPoints(files),
            DetectedFrameworks = DetectFrameworks(repoPath, files)
        };

        return overview;
    }

    private void ScanDirectory(string rootPath, string currentPath, List<FileInfo> files)
    {
        foreach (var dir in Directory.GetDirectories(currentPath))
        {
            var dirName = Path.GetFileName(dir);
            if (IgnoredDirectories.Contains(dirName))
                continue;

            ScanDirectory(rootPath, dir, files);
        }

        foreach (var filePath in Directory.GetFiles(currentPath))
        {
            var ext = Path.GetExtension(filePath);
            if (!ExtensionToLanguage.TryGetValue(ext, out var language))
                continue;

            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            var lineCount = File.ReadLines(filePath).Count();

            files.Add(new FileInfo
            {
                RelativePath = relativePath,
                Language = language,
                SizeBytes = new System.IO.FileInfo(filePath).Length,
                LineCount = lineCount
            });
        }
    }

    private static List<string> DetectEntryPoints(List<FileInfo> files)
    {
        var entryPatterns = new[] { "Program.cs", "Startup.cs", "index.ts", "index.js", "main.ts", "main.js", "App.tsx", "App.jsx" };
        return files
            .Where(f => entryPatterns.Any(p => f.RelativePath.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.RelativePath)
            .ToList();
    }

    private static List<string> DetectFrameworks(string repoPath, List<FileInfo> files)
    {
        var frameworks = new List<string>();

        if (files.Any(f => f.RelativePath.EndsWith(".csproj")))
            frameworks.Add(".NET");
        if (files.Any(f => f.RelativePath.EndsWith("package.json")))
            frameworks.Add("Node.js");
        if (File.Exists(Path.Combine(repoPath, "tsconfig.json")))
            frameworks.Add("TypeScript");

        return frameworks;
    }
}
