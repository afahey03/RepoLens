using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses R source files using regex-based text analysis.
/// Extracts library/require calls, source() calls, function
/// definitions (regular and assigned), S4/R5 class definitions,
/// and setGeneric/setMethod declarations.
/// </summary>
public class RParser : ILanguageParser
{
    private readonly ILogger<RParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "renv", ".Rproj.user", "packrat"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> RExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".r", ".R", ".Rmd"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex LibraryRegex = new(
        @"^\s*(?:library|require)\s*\(\s*['""]?(\w+)['""]?\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex SourceRegex = new(
        @"^\s*source\s*\(\s*['""]([^'""]+)['""]\s*\)",
        RegexOptions.Compiled);

    // name <- function(...)  or  name = function(...)
    private static readonly Regex FuncAssignRegex = new(
        @"^\s*(\w[\w.]*)\s*(?:<-|=)\s*function\s*\(",
        RegexOptions.Compiled);

    // setClass("ClassName", ...)
    private static readonly Regex SetClassRegex = new(
        @"^\s*setClass\s*\(\s*['""](\w+)['""]",
        RegexOptions.Compiled);

    // setRefClass("ClassName", ...)
    private static readonly Regex SetRefClassRegex = new(
        @"^\s*setRefClass\s*\(\s*['""](\w+)['""]",
        RegexOptions.Compiled);

    // R6Class("ClassName", ...)
    private static readonly Regex R6ClassRegex = new(
        @"(\w+)\s*(?:<-|=)\s*R6(?:::R6)?Class\s*\(\s*['""](\w+)['""]",
        RegexOptions.Compiled);

    // setGeneric("name", ...)
    private static readonly Regex SetGenericRegex = new(
        @"^\s*setGeneric\s*\(\s*['""](\w+)['""]",
        RegexOptions.Compiled);

    // setMethod("name", ...)
    private static readonly Regex SetMethodRegex = new(
        @"^\s*setMethod\s*\(\s*['""](\w+)['""]\s*,\s*['""](\w+)['""]",
        RegexOptions.Compiled);

    // contains = "ParentClass"
    private static readonly Regex ContainsRegex = new(
        @"contains\s*=\s*['""](\w+)['""]",
        RegexOptions.Compiled);

    // inherit = ParentClass
    private static readonly Regex InheritRegex = new(
        @"inherit\s*=\s*(\w+)",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "R" };

    public RParser(ILogger<RParser> logger) => _logger = logger;

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
        _logger.LogDebug("RParser: found {Count} R files", files.Count);

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

        _logger.LogInformation("RParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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

        string? lastClassName = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('#')) continue;

            // library / require
            var libMatch = LibraryRegex.Match(line);
            if (libMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = libMatch.Groups[1].Value, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // source("file.R")
            var srcMatch = SourceRegex.Match(line);
            if (srcMatch.Success)
            {
                var srcPath = srcMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = srcPath, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveSource(edges, moduleId, srcPath, relativePath, allRelativePaths);
                continue;
            }

            // setClass
            var setClassMatch = SetClassRegex.Match(line);
            if (setClassMatch.Success)
            {
                var className = setClassMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{className}";
                lastClassName = className;

                symbols.Add(new SymbolInfo { Name = className, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = className, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                // Check for contains = "Parent" in same/nearby lines
                var containsMatch = ContainsRegex.Match(line);
                if (containsMatch.Success)
                {
                    edges.Add(new GraphEdge { Source = nodeId, Target = $"class:{containsMatch.Groups[1].Value}", Relationship = EdgeRelationship.Inherits });
                }
                continue;
            }

            // setRefClass
            var setRefMatch = SetRefClassRegex.Match(line);
            if (setRefMatch.Success)
            {
                var className = setRefMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{className}";
                lastClassName = className;

                symbols.Add(new SymbolInfo { Name = className, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = className, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                var containsMatch = ContainsRegex.Match(line);
                if (containsMatch.Success)
                {
                    edges.Add(new GraphEdge { Source = nodeId, Target = $"class:{containsMatch.Groups[1].Value}", Relationship = EdgeRelationship.Inherits });
                }
                continue;
            }

            // R6Class
            var r6Match = R6ClassRegex.Match(line);
            if (r6Match.Success)
            {
                var className = r6Match.Groups[2].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{className}";
                lastClassName = className;

                symbols.Add(new SymbolInfo { Name = className, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = className, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                var inheritMatch = InheritRegex.Match(line);
                if (inheritMatch.Success)
                {
                    edges.Add(new GraphEdge { Source = nodeId, Target = $"class:{inheritMatch.Groups[1].Value}", Relationship = EdgeRelationship.Inherits });
                }
                continue;
            }

            // setGeneric
            var setGenMatch = SetGenericRegex.Match(line);
            if (setGenMatch.Success)
            {
                var funcName = setGenMatch.Groups[1].Value;
                var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
                symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = funcNodeId, Name = funcName, Type = NodeType.Function, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // setMethod
            var setMethodMatch = SetMethodRegex.Match(line);
            if (setMethodMatch.Success)
            {
                var methodName = setMethodMatch.Groups[1].Value;
                var forClass = setMethodMatch.Groups[2].Value;
                symbols.Add(new SymbolInfo { Name = methodName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = forClass });
                continue;
            }

            // name <- function(...)
            var funcMatch = FuncAssignRegex.Match(line);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
                symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = funcNodeId, Name = funcName, Type = NodeType.Function, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static void ResolveSource(
        List<GraphEdge> edges, string sourceModuleId, string srcPath,
        string currentFile, HashSet<string> allRelativePaths)
    {
        var dir = Path.GetDirectoryName(currentFile)?.Replace('\\', '/') ?? "";
        var candidate = string.IsNullOrEmpty(dir) ? srcPath : $"{dir}/{srcPath}";
        candidate = NormalizePath(candidate);

        foreach (var rp in allRelativePaths)
        {
            if (rp.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(new GraphEdge { Source = sourceModuleId, Target = $"module:{StripExtension(rp)}", Relationship = EdgeRelationship.Imports });
                return;
            }
        }
    }

    private static string NormalizePath(string path)
    {
        var parts = path.Split('/').ToList();
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".." && result.Count > 0) result.RemoveAt(result.Count - 1);
            else if (part != ".") result.Add(part);
        }
        return string.Join('/', result);
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
                var ext = Path.GetExtension(file);
                if (ext.Equals(".r", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".R", StringComparison.Ordinal) ||
                    ext.Equals(".Rmd", StringComparison.OrdinalIgnoreCase))
                    result.Add(file);
            }
        }
        catch (UnauthorizedAccessException) { }
    }

    private record CombinedResult(List<SymbolInfo> Symbols, List<GraphNode> Nodes, List<GraphEdge> Edges);
    private record FileParseResult(List<SymbolInfo> Symbols, List<GraphNode> Nodes, List<GraphEdge> Edges);
}
