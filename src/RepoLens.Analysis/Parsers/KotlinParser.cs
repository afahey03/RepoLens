using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Kotlin source files using regex-based text analysis.
/// Extracts imports, packages, classes, data classes, objects, interfaces,
/// enums, functions, and properties. Builds dependency graph nodes and
/// edges for containment, imports, inheritance, and interface implementation.
/// </summary>
public class KotlinParser : ILanguageParser
{
    private readonly ILogger<KotlinParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "build", ".gradle", ".kotlin", "target", "out"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".kt", ".kts" };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex PackageRegex = new(
        @"^\s*package\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex ImportRegex = new(
        @"^\s*import\s+([\w.]+(?:\.\*)?)",
        RegexOptions.Compiled);

    private static readonly Regex ClassRegex = new(
        @"^\s*(?:(?:abstract|open|sealed|inner|data|annotation|private|protected|internal|public|actual|expect)\s+)*class\s+(\w+)(?:\s*<[^>]+>)?(?:\s*\([^)]*\))?\s*(?::\s*(.+?))?\s*[\{$]",
        RegexOptions.Compiled);

    private static readonly Regex ObjectRegex = new(
        @"^\s*(?:companion\s+)?object\s+(\w+)?(?:\s*:\s*(.+?))?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex InterfaceRegex = new(
        @"^\s*(?:(?:sealed|private|protected|internal|public|fun)\s+)*interface\s+(\w+)(?:\s*<[^>]+>)?(?:\s*:\s*(.+?))?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex EnumRegex = new(
        @"^\s*(?:(?:private|protected|internal|public)\s+)*enum\s+class\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex FunRegex = new(
        @"^\s*(?:(?:override|open|abstract|final|private|protected|internal|public|inline|suspend|operator|infix|tailrec|actual|expect)\s+)*fun\s+(?:<[^>]+>\s+)?(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex PropertyRegex = new(
        @"^\s*(?:(?:override|open|abstract|final|private|protected|internal|public|const|lateinit|actual|expect)\s+)*(?:val|var)\s+(\w+)\s*[:\=]",
        RegexOptions.Compiled);

    private static readonly Regex TypeAliasRegex = new(
        @"^\s*typealias\s+(\w+)\s*=",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Kotlin" };

    public KotlinParser(ILogger<KotlinParser> logger) => _logger = logger;

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
        _logger.LogDebug("KotlinParser: found {Count} Kotlin files", files.Count);

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

        _logger.LogInformation("KotlinParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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

            // interface
            var ifaceMatch = InterfaceRegex.Match(line);
            if (ifaceMatch.Success)
            {
                var name = ifaceMatch.Groups[1].Value;
                var nodeId = $"interface:{StripExtension(relativePath)}.{name}";
                currentType = name;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Interface, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Interface, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (ifaceMatch.Groups[2].Success)
                    AddSuperTypes(edges, nodeId, ifaceMatch.Groups[2].Value);
                continue;
            }

            // enum class
            var enumMatch = EnumRegex.Match(line);
            if (enumMatch.Success)
            {
                var name = enumMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                currentType = name;
                continue;
            }

            // class / data class
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
                    AddSuperTypes(edges, nodeId, classMatch.Groups[2].Value);
                continue;
            }

            // object
            var objMatch = ObjectRegex.Match(line);
            if (objMatch.Success && objMatch.Groups[1].Success)
            {
                var name = objMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                currentType = name;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["kotlinKind"] = "object" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // typealias
            var taMatch = TypeAliasRegex.Match(line);
            if (taMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = taMatch.Groups[1].Value, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // fun
            var funMatch = FunRegex.Match(line);
            if (funMatch.Success)
            {
                var funcName = funMatch.Groups[1].Value;
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

            // val / var
            var propMatch = PropertyRegex.Match(line);
            if (propMatch.Success)
            {
                var name = propMatch.Groups[1].Value;
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

    private static void AddSuperTypes(List<GraphEdge> edges, string nodeId, string supers)
    {
        // Kotlin: MyBase(), InterfaceA, InterfaceB
        foreach (var part in supers.Split(',', StringSplitOptions.TrimEntries))
        {
            var name = part.Split('(')[0].Split('<')[0].Trim();
            if (string.IsNullOrEmpty(name)) continue;
            // If it ends with (), it's a class; otherwise could be interface
            var rel = part.Contains('(') ? EdgeRelationship.Inherits : EdgeRelationship.Implements;
            var prefix = part.Contains('(') ? "class" : "interface";
            edges.Add(new GraphEdge { Source = nodeId, Target = $"{prefix}:{name}", Relationship = rel });
        }
    }

    private static void ResolveImport(
        List<GraphEdge> edges, string sourceModuleId, string importPath,
        HashSet<string> allRelativePaths)
    {
        var filePath = importPath.Replace('.', '/').TrimEnd('*').TrimEnd('.');
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
