using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Swift source files using regex-based text analysis.
/// Extracts imports, classes, structs, enums, protocols, extensions,
/// functions, properties, and typealiases. Builds dependency graph
/// nodes and edges for containment, imports, and inheritance/conformance.
/// </summary>
public class SwiftParser : ILanguageParser
{
    private readonly ILogger<SwiftParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        ".build", "DerivedData", "Pods", "Carthage", ".swiftpm"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".swift" };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex ImportRegex = new(
        @"^\s*import\s+(\w[\w.]*)",
        RegexOptions.Compiled);

    private static readonly Regex ClassRegex = new(
        @"^\s*(?:(?:open|public|internal|fileprivate|private|final)\s+)*class\s+(\w+)(?:\s*<[^>]+>)?(?:\s*:\s*(.+?))?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex StructRegex = new(
        @"^\s*(?:(?:public|internal|fileprivate|private)\s+)*struct\s+(\w+)(?:\s*<[^>]+>)?(?:\s*:\s*(.+?))?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex EnumRegex = new(
        @"^\s*(?:(?:public|internal|fileprivate|private)\s+)*enum\s+(\w+)(?:\s*<[^>]+>)?(?:\s*:\s*(.+?))?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex ProtocolRegex = new(
        @"^\s*(?:(?:public|internal|fileprivate|private)\s+)*protocol\s+(\w+)(?:\s*:\s*(.+?))?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex ExtensionRegex = new(
        @"^\s*(?:(?:public|internal|fileprivate|private)\s+)*extension\s+(\w+)(?:\s*:\s*(.+?))?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex FuncRegex = new(
        @"^\s*(?:(?:open|public|internal|fileprivate|private|static|class|override|mutating)\s+)*func\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex PropertyRegex = new(
        @"^\s*(?:(?:open|public|internal|fileprivate|private|static|class|lazy|weak|unowned)\s+)*(?:var|let)\s+(\w+)\s*[:\=]",
        RegexOptions.Compiled);

    private static readonly Regex TypealiasRegex = new(
        @"^\s*(?:(?:public|internal|fileprivate|private)\s+)*typealias\s+(\w+)\s*=",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Swift" };

    public SwiftParser(ILogger<SwiftParser> logger) => _logger = logger;

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
        _logger.LogDebug("SwiftParser: found {Count} .swift files", files.Count);

        var allSymbols = new List<SymbolInfo>();
        var allNodes = new List<GraphNode>();
        var allEdges = new List<GraphEdge>();

        foreach (var file in files)
        {
            var fi = new System.IO.FileInfo(file);
            if (fi.Length > MaxParseFileSize) continue;

            var rel = Path.GetRelativePath(repoPath, file).Replace('\\', '/');
            var parsed = ParseFile(rel, file);
            allSymbols.AddRange(parsed.Symbols);
            allNodes.AddRange(parsed.Nodes);
            allEdges.AddRange(parsed.Edges);
        }

        _logger.LogInformation("SwiftParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
            allSymbols.Count, allNodes.Count, allEdges.Count);

        _lastRepoPath = repoPath;
        _lastResult = new CombinedResult(allSymbols, allNodes, allEdges);
        return Task.FromResult(_lastResult);
    }

    private static FileParseResult ParseFile(string relativePath, string absolutePath)
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

            // import
            var importMatch = ImportRegex.Match(line);
            if (importMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = importMatch.Groups[1].Value, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // protocol (before class, since pattern is similar)
            var protoMatch = ProtocolRegex.Match(line);
            if (protoMatch.Success)
            {
                var name = protoMatch.Groups[1].Value;
                var nodeId = $"interface:{StripExtension(relativePath)}.{name}";
                currentType = name;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Interface, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Interface, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (protoMatch.Groups[2].Success)
                    AddConformanceEdges(edges, nodeId, protoMatch.Groups[2].Value);
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
                    AddInheritanceEdges(edges, nodeId, classMatch.Groups[2].Value);
                continue;
            }

            // struct
            var structMatch = StructRegex.Match(line);
            if (structMatch.Success)
            {
                var name = structMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                currentType = name;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["swiftKind"] = "struct" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (structMatch.Groups[2].Success)
                    AddConformanceEdges(edges, nodeId, structMatch.Groups[2].Value);
                continue;
            }

            // enum
            var enumMatch = EnumRegex.Match(line);
            if (enumMatch.Success)
            {
                var name = enumMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // extension
            var extMatch = ExtensionRegex.Match(line);
            if (extMatch.Success)
            {
                currentType = extMatch.Groups[1].Value;
                continue;
            }

            // typealias
            var taMatch = TypealiasRegex.Match(line);
            if (taMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = taMatch.Groups[1].Value, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // func
            var funcMatch = FuncRegex.Match(line);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                if (currentType is not null)
                {
                    symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = currentType });
                }
                else
                {
                    var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
                    symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                    nodes.Add(new GraphNode { Id = funcNodeId, Name = funcName, Type = NodeType.Function, FilePath = relativePath });
                    edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                }
                continue;
            }

            // property
            var propMatch = PropertyRegex.Match(line);
            if (propMatch.Success)
            {
                var propName = propMatch.Groups[1].Value;
                if (currentType is not null)
                {
                    symbols.Add(new SymbolInfo { Name = propName, Kind = SymbolKind.Property, FilePath = relativePath, Line = lineNum, ParentSymbol = currentType });
                }
                else
                {
                    symbols.Add(new SymbolInfo { Name = propName, Kind = SymbolKind.Variable, FilePath = relativePath, Line = lineNum });
                }
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static void AddInheritanceEdges(List<GraphEdge> edges, string classNodeId, string bases)
    {
        var parts = bases.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;

        // First is superclass in Swift
        edges.Add(new GraphEdge { Source = classNodeId, Target = $"class:{parts[0].Trim()}", Relationship = EdgeRelationship.Inherits });
        for (var j = 1; j < parts.Length; j++)
        {
            var proto = parts[j].Trim();
            if (!string.IsNullOrEmpty(proto))
                edges.Add(new GraphEdge { Source = classNodeId, Target = $"interface:{proto}", Relationship = EdgeRelationship.Implements });
        }
    }

    private static void AddConformanceEdges(List<GraphEdge> edges, string nodeId, string protocols)
    {
        foreach (var proto in protocols.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrEmpty(proto))
                edges.Add(new GraphEdge { Source = nodeId, Target = $"interface:{proto}", Relationship = EdgeRelationship.Implements });
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
