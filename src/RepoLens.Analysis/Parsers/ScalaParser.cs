using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Scala source files using regex-based text analysis.
/// Extracts imports, packages, classes, objects, traits, case classes,
/// methods, and vals/vars. Builds dependency graph nodes and edges for
/// containment, imports, inheritance, and trait mixing.
/// </summary>
public class ScalaParser : ILanguageParser
{
    private readonly ILogger<ScalaParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "target", ".bsp", ".metals", ".bloop", "project/target"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".scala", ".sc" };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex PackageRegex = new(
        @"^\s*package\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex ImportRegex = new(
        @"^\s*import\s+([\w.]+(?:\.\{[^}]+\}|\.\*|\._)?)",
        RegexOptions.Compiled);

    private static readonly Regex ClassRegex = new(
        @"^\s*(?:(?:abstract|final|sealed|implicit|lazy|private|protected)\s+)*class\s+(\w+)(?:\[.*?\])?(?:\s*\([^)]*\))?(?:\s+extends\s+(\w[\w.]*))?(?:\s+with\s+([\w.\s,]+?))?(?:\s*\{|$)",
        RegexOptions.Compiled);

    private static readonly Regex CaseClassRegex = new(
        @"^\s*(?:(?:abstract|sealed)\s+)?case\s+class\s+(\w+)(?:\[.*?\])?\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ObjectRegex = new(
        @"^\s*(?:case\s+)?object\s+(\w+)(?:\s+extends\s+(\w[\w.]*))?",
        RegexOptions.Compiled);

    private static readonly Regex TraitRegex = new(
        @"^\s*(?:sealed\s+)?trait\s+(\w+)(?:\[.*?\])?(?:\s+extends\s+(\w[\w.]*))?",
        RegexOptions.Compiled);

    private static readonly Regex DefRegex = new(
        @"^\s*(?:(?:override|private|protected|implicit|final|lazy)\s+)*def\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex ValVarRegex = new(
        @"^\s*(?:(?:override|private|protected|implicit|final|lazy)\s+)*(?:val|var)\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex TypeRegex = new(
        @"^\s*type\s+(\w+)",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Scala" };

    public ScalaParser(ILogger<ScalaParser> logger) => _logger = logger;

    private string? _lastRepoPath;
    private CombinedResult? _lastResult;

    public async Task<List<SymbolInfo>> ExtractSymbolsAsync(
        string repoPath, CancellationToken cancellationToken = default)
    {
        var result = await ParseRepositoryAsync(repoPath);
        return result.Symbols;
    }

    public async Task<(List<GraphNode> Nodes, List<GraphEdge> Edges)> BuildDependenciesAsync(
        string repoPath, CancellationToken cancellationToken = default)
    {
        var result = await ParseRepositoryAsync(repoPath);
        return (result.Nodes, result.Edges);
    }

    private Task<CombinedResult> ParseRepositoryAsync(string repoPath)
    {
        if (_lastRepoPath == repoPath && _lastResult is not null)
            return Task.FromResult(_lastResult);

        var files = FindFiles(repoPath);
        _logger.LogDebug("ScalaParser: found {Count} Scala files", files.Count);

        var allSymbols = new List<SymbolInfo>();
        var allNodes = new List<GraphNode>();
        var allEdges = new List<GraphEdge>();

        var allRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            allRelativePaths.Add(Path.GetRelativePath(repoPath, f).Replace('\\', '/'));

        foreach (var file in files)
        {
            var fi = new System.IO.FileInfo(file);
            if (fi.Length > MaxParseFileSize) continue;

            var rel = Path.GetRelativePath(repoPath, file).Replace('\\', '/');
            var parsed = ParseFile(rel, file, allRelativePaths);
            allSymbols.AddRange(parsed.Symbols);
            allNodes.AddRange(parsed.Nodes);
            allEdges.AddRange(parsed.Edges);
        }

        _logger.LogInformation("ScalaParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
            allSymbols.Count, allNodes.Count, allEdges.Count);

        _lastRepoPath = repoPath;
        _lastResult = new CombinedResult(allSymbols, allNodes, allEdges);
        return Task.FromResult(_lastResult);
    }

    private static FileParseResult ParseFile(
        string relativePath, string absolutePath, HashSet<string> allRelativePaths)
    {
        var symbols = new List<SymbolInfo>();
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        string[] lines;
        try { lines = File.ReadAllLines(absolutePath); }
        catch { return new FileParseResult(symbols, nodes, edges); }

        var moduleId = $"module:{StripExtension(relativePath)}";
        nodes.Add(new GraphNode
        {
            Id = moduleId,
            Name = Path.GetFileNameWithoutExtension(relativePath),
            Type = NodeType.Module,
            FilePath = relativePath
        });

        string? currentType = null;
        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            if (inBlockComment)
            {
                if (trimmed.Contains("*/")) inBlockComment = false;
                continue;
            }
            if (trimmed.StartsWith("/*"))
            {
                if (!trimmed.Contains("*/")) inBlockComment = true;
                continue;
            }
            if (trimmed.StartsWith("//")) continue;

            // package
            var pkgMatch = PackageRegex.Match(line);
            if (pkgMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = pkgMatch.Groups[1].Value, Kind = SymbolKind.Namespace, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // import
            var importMatch = ImportRegex.Match(line);
            if (importMatch.Success)
            {
                var importPath = importMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = importPath, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveImport(edges, moduleId, importPath, allRelativePaths);
                continue;
            }

            // trait
            var traitMatch = TraitRegex.Match(line);
            if (traitMatch.Success)
            {
                var name = traitMatch.Groups[1].Value;
                var nodeId = $"interface:{StripExtension(relativePath)}.{name}";
                currentType = name;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Interface, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Interface, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (traitMatch.Groups[2].Success)
                    edges.Add(new GraphEdge { Source = nodeId, Target = $"interface:{traitMatch.Groups[2].Value}", Relationship = EdgeRelationship.Inherits });
                continue;
            }

            // case class
            var ccMatch = CaseClassRegex.Match(line);
            if (ccMatch.Success)
            {
                var name = ccMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                currentType = name;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["scalaKind"] = "case class" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // class
            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                currentType = name;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (classMatch.Groups[2].Success)
                    edges.Add(new GraphEdge { Source = nodeId, Target = $"class:{classMatch.Groups[2].Value}", Relationship = EdgeRelationship.Inherits });
                if (classMatch.Groups[3].Success)
                {
                    foreach (var t in classMatch.Groups[3].Value.Split(new[] { " with ", "," }, StringSplitOptions.TrimEntries))
                    {
                        if (!string.IsNullOrEmpty(t))
                            edges.Add(new GraphEdge { Source = nodeId, Target = $"interface:{t.Trim()}", Relationship = EdgeRelationship.Implements });
                    }
                }
                continue;
            }

            // object
            var objMatch = ObjectRegex.Match(line);
            if (objMatch.Success)
            {
                var name = objMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                currentType = name;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["scalaKind"] = "object" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (objMatch.Groups[2].Success)
                    edges.Add(new GraphEdge { Source = nodeId, Target = $"class:{objMatch.Groups[2].Value}", Relationship = EdgeRelationship.Inherits });
                continue;
            }

            // type alias
            var typeMatch = TypeRegex.Match(line);
            if (typeMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = typeMatch.Groups[1].Value, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // def
            var defMatch = DefRegex.Match(line);
            if (defMatch.Success)
            {
                var name = defMatch.Groups[1].Value;
                if (currentType is not null)
                {
                    symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = currentType });
                }
                else
                {
                    symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                }
                continue;
            }

            // val / var
            var valMatch = ValVarRegex.Match(line);
            if (valMatch.Success)
            {
                var name = valMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = name,
                    Kind = currentType is not null ? SymbolKind.Property : SymbolKind.Variable,
                    FilePath = relativePath,
                    Line = lineNum,
                    ParentSymbol = currentType
                });
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static void ResolveImport(
        List<GraphEdge> edges, string sourceModuleId, string importPath,
        HashSet<string> allRelativePaths)
    {
        // Convert com.example.MyClass to com/example/MyClass.scala
        var filePath = importPath.Replace('.', '/').Split('{')[0].TrimEnd('.');
        foreach (var rp in allRelativePaths)
        {
            var stripped = StripExtension(rp);
            if (stripped.EndsWith(filePath, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(new GraphEdge { Source = sourceModuleId, Target = $"module:{StripExtension(rp)}", Relationship = EdgeRelationship.Imports });
                return;
            }
        }
    }

    private static string StripExtension(string path)
    {
        var lastDot = path.LastIndexOf('.');
        var lastSlash = path.LastIndexOf('/');
        return lastDot > lastSlash ? path[..lastDot] : path;
    }

    private static List<string> FindFiles(string rootPath)
    {
        var result = new List<string>();
        CollectFiles(rootPath, result);
        return result;
    }

    private static void CollectFiles(string dir, List<string> result)
    {
        try
        {
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(subDir);
                if (IgnoredDirs.Contains(name) || name.StartsWith('.')) continue;
                CollectFiles(subDir, result);
            }
            foreach (var file in Directory.GetFiles(dir))
            {
                if (Extensions.Contains(Path.GetExtension(file)))
                    result.Add(file);
            }
        }
        catch (UnauthorizedAccessException) { }
    }

    private record CombinedResult(List<SymbolInfo> Symbols, List<GraphNode> Nodes, List<GraphEdge> Edges);
    private record FileParseResult(List<SymbolInfo> Symbols, List<GraphNode> Nodes, List<GraphEdge> Edges);
}
