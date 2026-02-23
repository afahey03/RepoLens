using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses C source files using regex-based text analysis.
/// Extracts #include directives, struct/union/enum/typedef declarations,
/// function definitions, and global variables. Builds dependency graph
/// nodes and edges for containment and imports.
/// </summary>
public class CParser : ILanguageParser
{
    private readonly ILogger<CParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "build", "cmake-build-debug", "cmake-build-release", ".cache",
        "third_party", "external", "deps"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex IncludeRegex = new(
        @"^\s*#\s*include\s+[""<]([^"">]+)[>""]",
        RegexOptions.Compiled);

    private static readonly Regex StructRegex = new(
        @"^\s*(?:typedef\s+)?struct\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex UnionRegex = new(
        @"^\s*(?:typedef\s+)?union\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex EnumRegex = new(
        @"^\s*(?:typedef\s+)?enum\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex TypedefRegex = new(
        @"^\s*typedef\s+.+\s+(\w+)\s*;",
        RegexOptions.Compiled);

    /// <summary>Top-level function definition (not indented, not a macro).</summary>
    private static readonly Regex FunctionRegex = new(
        @"^(?!#)(?:(?:static|inline|extern|const|unsigned|signed|void|int|char|short|long|float|double|size_t|ssize_t|bool|_Bool|struct\s+\w+\s*\*?|enum\s+\w+|[\w_]+)\s+)+\*?\s*(\w+)\s*\([^)]*\)\s*\{?$",
        RegexOptions.Compiled);

    /// <summary>Simpler function pattern for declarations ending in {</summary>
    private static readonly Regex SimpleFuncRegex = new(
        @"^(?:[\w*\s]+)\s+(\w+)\s*\([^;]*\)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex DefineRegex = new(
        @"^\s*#\s*define\s+(\w+)",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C" };

    public CParser(ILogger<CParser> logger) => _logger = logger;

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
        _logger.LogDebug("CParser: found {Count} C files", files.Count);

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

        _logger.LogInformation("CParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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

        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            // Block comment tracking
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

            // #include
            var includeMatch = IncludeRegex.Match(line);
            if (includeMatch.Success)
            {
                var header = includeMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = header, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveInclude(edges, moduleId, header, relativePath, allRelativePaths);
                continue;
            }

            // #define
            var defineMatch = DefineRegex.Match(line);
            if (defineMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = defineMatch.Groups[1].Value, Kind = SymbolKind.Variable, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // struct
            var structMatch = StructRegex.Match(line);
            if (structMatch.Success)
            {
                var name = structMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["cKind"] = "struct" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // union
            var unionMatch = UnionRegex.Match(line);
            if (unionMatch.Success)
            {
                var name = unionMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
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

            // function definition
            var funcMatch = SimpleFuncRegex.Match(line);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                if (IsValidCFunctionName(funcName))
                {
                    var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
                    symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                    nodes.Add(new GraphNode { Id = funcNodeId, Name = funcName, Type = NodeType.Function, FilePath = relativePath });
                    edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                }
                continue;
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static bool IsValidCFunctionName(string name)
    {
        // Filter out C keywords and common false positives
        return name switch
        {
            "if" or "else" or "for" or "while" or "switch" or "case" or "return" or
            "break" or "continue" or "goto" or "do" or "sizeof" or "typedef" or
            "struct" or "union" or "enum" or "static" or "extern" or "inline" or
            "const" or "volatile" or "register" or "auto" or "void" or "define" => false,
            _ => true
        };
    }

    private static void ResolveInclude(
        List<GraphEdge> edges, string sourceModuleId, string header,
        string currentFile, HashSet<string> allRelativePaths)
    {
        // Try to resolve "header.h" to a file in the repo
        var fileName = Path.GetFileName(header);
        foreach (var rp in allRelativePaths)
        {
            if (rp.EndsWith(header, StringComparison.OrdinalIgnoreCase) ||
                rp.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                if (rp.Equals(currentFile, StringComparison.OrdinalIgnoreCase)) continue;
                var targetId = $"module:{StripExtension(rp)}";
                edges.Add(new GraphEdge { Source = sourceModuleId, Target = targetId, Relationship = EdgeRelationship.Imports });
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
