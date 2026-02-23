using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses PHP source files using regex-based text analysis.
/// Extracts use/namespace/require/include, classes, interfaces, traits,
/// enums, functions, and methods. Builds dependency graph nodes and
/// edges for containment, imports, inheritance, and trait usage.
/// </summary>
public class PhpParser : ILanguageParser
{
    private readonly ILogger<PhpParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "vendor", "cache", "storage", ".phpunit.cache"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".php" };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex NamespaceRegex = new(
        @"^\s*namespace\s+([\w\\]+)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex UseRegex = new(
        @"^\s*use\s+([\w\\]+)(?:\s+as\s+\w+)?\s*;",
        RegexOptions.Compiled);

    private static readonly Regex RequireIncludeRegex = new(
        @"^\s*(?:require|include)(?:_once)?\s*[\(]?\s*['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex ClassRegex = new(
        @"^\s*(?:(?:abstract|final)\s+)?class\s+(\w+)(?:\s+extends\s+(\w+))?(?:\s+implements\s+([\w,\s\\]+))?\s*\{?",
        RegexOptions.Compiled);

    private static readonly Regex InterfaceRegex = new(
        @"^\s*interface\s+(\w+)(?:\s+extends\s+([\w,\s\\]+))?\s*\{?",
        RegexOptions.Compiled);

    private static readonly Regex TraitRegex = new(
        @"^\s*trait\s+(\w+)\s*\{?",
        RegexOptions.Compiled);

    private static readonly Regex EnumRegex = new(
        @"^\s*enum\s+(\w+)(?:\s*:\s*\w+)?\s*(?:implements\s+([\w,\s\\]+))?\s*\{?",
        RegexOptions.Compiled);

    private static readonly Regex FunctionRegex = new(
        @"^\s*(?:(?:public|protected|private|static|abstract|final)\s+)*function\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex TopLevelFunctionRegex = new(
        @"^function\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ConstRegex = new(
        @"^\s*(?:(?:public|protected|private)\s+)?const\s+(\w+)\s*=",
        RegexOptions.Compiled);

    private static readonly Regex UseTraitRegex = new(
        @"^\s*use\s+(\w+(?:\s*,\s*\w+)*)\s*;",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PHP" };

    public PhpParser(ILogger<PhpParser> logger) => _logger = logger;

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
        _logger.LogDebug("PhpParser: found {Count} PHP files", files.Count);

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

        _logger.LogInformation("PhpParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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
            Name = Path.GetFileName(relativePath),
            Type = NodeType.Module,
            FilePath = relativePath
        });

        string? currentType = null;
        var inClass = false;
        var braceDepth = 0;
        var classDepth = 0;
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
            if (trimmed.StartsWith("//") || trimmed.StartsWith('#')) continue;

            // Track brace depth for class scope
            braceDepth += line.Count(c => c == '{') - line.Count(c => c == '}');
            if (inClass && braceDepth <= classDepth)
            {
                inClass = false;
                currentType = null;
            }

            // namespace
            var nsMatch = NamespaceRegex.Match(line);
            if (nsMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = nsMatch.Groups[1].Value.Replace("\\", "."), Kind = SymbolKind.Namespace, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // use (namespace import, not trait usage inside class)
            if (!inClass)
            {
                var useMatch = UseRegex.Match(line);
                if (useMatch.Success)
                {
                    var usePath = useMatch.Groups[1].Value;
                    symbols.Add(new SymbolInfo { Name = usePath.Replace("\\", "."), Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                    ResolveUse(edges, moduleId, usePath, allRelativePaths);
                    continue;
                }
            }

            // require / include
            var reqMatch = RequireIncludeRegex.Match(line);
            if (reqMatch.Success)
            {
                var path = reqMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = path, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveFile(edges, moduleId, path, relativePath, allRelativePaths);
                continue;
            }

            // interface
            var ifaceMatch = InterfaceRegex.Match(line);
            if (ifaceMatch.Success)
            {
                var name = ifaceMatch.Groups[1].Value;
                var nodeId = $"interface:{StripExtension(relativePath)}.{name}";
                currentType = name;
                inClass = true;
                classDepth = braceDepth - 1;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Interface, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Interface, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (ifaceMatch.Groups[2].Success)
                {
                    foreach (var parent in ifaceMatch.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries))
                    {
                        var pName = parent.Split('\\').Last().Trim();
                        if (!string.IsNullOrEmpty(pName))
                            edges.Add(new GraphEdge { Source = nodeId, Target = $"interface:{pName}", Relationship = EdgeRelationship.Inherits });
                    }
                }
                continue;
            }

            // trait
            var traitMatch = TraitRegex.Match(line);
            if (traitMatch.Success)
            {
                var name = traitMatch.Groups[1].Value;
                currentType = name;
                inClass = true;
                classDepth = braceDepth - 1;

                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["phpKind"] = "trait" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // enum
            var enumMatch = EnumRegex.Match(line);
            if (enumMatch.Success)
            {
                var name = enumMatch.Groups[1].Value;
                currentType = name;
                inClass = true;
                classDepth = braceDepth - 1;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // class
            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                currentType = name;
                inClass = true;
                classDepth = braceDepth - 1;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (classMatch.Groups[2].Success)
                {
                    var baseName = classMatch.Groups[2].Value.Split('\\').Last().Trim();
                    if (!string.IsNullOrEmpty(baseName))
                        edges.Add(new GraphEdge { Source = nodeId, Target = $"class:{baseName}", Relationship = EdgeRelationship.Inherits });
                }
                if (classMatch.Groups[3].Success)
                {
                    foreach (var iface in classMatch.Groups[3].Value.Split(',', StringSplitOptions.TrimEntries))
                    {
                        var ifaceName = iface.Split('\\').Last().Trim();
                        if (!string.IsNullOrEmpty(ifaceName))
                            edges.Add(new GraphEdge { Source = nodeId, Target = $"interface:{ifaceName}", Relationship = EdgeRelationship.Implements });
                    }
                }
                continue;
            }

            // function/method
            var funcMatch = FunctionRegex.Match(line);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                if (funcName == "__construct" || funcName == "__destruct") continue;

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

            // const
            var constMatch = ConstRegex.Match(line);
            if (constMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = constMatch.Groups[1].Value, Kind = SymbolKind.Variable, FilePath = relativePath, Line = lineNum, ParentSymbol = currentType });
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static void ResolveUse(
        List<GraphEdge> edges, string sourceModuleId, string usePath,
        HashSet<string> allRelativePaths)
    {
        // Convert App\Models\User to App/Models/User.php
        var filePath = usePath.Replace("\\", "/");
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

    private static void ResolveFile(
        List<GraphEdge> edges, string sourceModuleId, string includePath,
        string currentFile, HashSet<string> allRelativePaths)
    {
        var normalized = includePath.Replace("\\", "/").TrimStart('.', '/');
        var fileName = Path.GetFileName(normalized);
        foreach (var rp in allRelativePaths)
        {
            if (rp.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                rp.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                if (rp.Equals(currentFile, StringComparison.OrdinalIgnoreCase)) continue;
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
