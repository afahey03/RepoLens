using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Ruby source files using regex-based text analysis.
/// Extracts require/require_relative, modules, classes, methods,
/// attributes, and constants. Builds dependency graph nodes and
/// edges for containment, imports, inheritance, and module inclusion.
/// </summary>
public class RubyParser : ILanguageParser
{
    private readonly ILogger<RubyParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "vendor", ".bundle", "tmp", "log", "coverage", "spec/fixtures"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rb", ".rake", ".gemspec"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex RequireRegex = new(
        @"^\s*require\s+['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex RequireRelativeRegex = new(
        @"^\s*require_relative\s+['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex ModuleRegex = new(
        @"^\s*module\s+(\w+(?:::\w+)*)",
        RegexOptions.Compiled);

    private static readonly Regex ClassRegex = new(
        @"^\s*class\s+(\w+(?:::\w+)*)(?:\s*<\s*(\w+(?:::\w+)*))?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex DefRegex = new(
        @"^\s*def\s+(?:self\.)?(\w+[?!=]?)",
        RegexOptions.Compiled);

    private static readonly Regex AttrRegex = new(
        @"^\s*attr_(?:accessor|reader|writer)\s+(.+)",
        RegexOptions.Compiled);

    private static readonly Regex IncludeRegex = new(
        @"^\s*include\s+(\w+(?:::\w+)*)",
        RegexOptions.Compiled);

    private static readonly Regex ExtendRegex = new(
        @"^\s*extend\s+(\w+(?:::\w+)*)",
        RegexOptions.Compiled);

    private static readonly Regex ConstantRegex = new(
        @"^\s*([A-Z][A-Z0-9_]*)\s*=",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ruby" };

    public RubyParser(ILogger<RubyParser> logger) => _logger = logger;

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
        _logger.LogDebug("RubyParser: found {Count} Ruby files", files.Count);

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

        _logger.LogInformation("RubyParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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
        var inMultilineComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            if (inMultilineComment)
            {
                if (trimmed.StartsWith("=end")) inMultilineComment = false;
                continue;
            }
            if (trimmed.StartsWith("=begin"))
            {
                inMultilineComment = true;
                continue;
            }
            if (trimmed.StartsWith('#')) continue;

            // require
            var reqMatch = RequireRegex.Match(line);
            if (reqMatch.Success)
            {
                var reqPath = reqMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = reqPath, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveRequire(edges, moduleId, reqPath, allRelativePaths);
                continue;
            }

            // require_relative
            var reqRelMatch = RequireRelativeRegex.Match(line);
            if (reqRelMatch.Success)
            {
                var reqPath = reqRelMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = reqPath, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveRequireRelative(edges, moduleId, reqPath, relativePath, allRelativePaths);
                continue;
            }

            // module
            var modMatch = ModuleRegex.Match(line);
            if (modMatch.Success)
            {
                var modName = modMatch.Groups[1].Value;
                currentType = modName.Split("::").Last();
                symbols.Add(new SymbolInfo { Name = modName, Kind = SymbolKind.Namespace, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // class
            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups[1].Value;
                var simpleName = name.Split("::").Last();
                var nodeId = $"class:{StripExtension(relativePath)}.{simpleName}";
                currentType = simpleName;

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = simpleName, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (classMatch.Groups[2].Success)
                {
                    var baseName = classMatch.Groups[2].Value.Split("::").Last();
                    edges.Add(new GraphEdge { Source = nodeId, Target = $"class:{baseName}", Relationship = EdgeRelationship.Inherits });
                }
                continue;
            }

            // include / extend
            var includeMatch = IncludeRegex.Match(line);
            if (includeMatch.Success && currentType is not null)
            {
                var mixinName = includeMatch.Groups[1].Value.Split("::").Last();
                edges.Add(new GraphEdge
                {
                    Source = $"class:{StripExtension(relativePath)}.{currentType}",
                    Target = $"interface:{mixinName}",
                    Relationship = EdgeRelationship.Implements
                });
                continue;
            }

            var extendMatch = ExtendRegex.Match(line);
            if (extendMatch.Success && currentType is not null)
            {
                continue; // logged but no special edge
            }

            // def
            var defMatch = DefRegex.Match(line);
            if (defMatch.Success)
            {
                var methodName = defMatch.Groups[1].Value;
                if (currentType is not null)
                {
                    symbols.Add(new SymbolInfo { Name = methodName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = currentType });
                }
                else
                {
                    var funcNodeId = $"func:{StripExtension(relativePath)}.{methodName}";
                    symbols.Add(new SymbolInfo { Name = methodName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                    nodes.Add(new GraphNode { Id = funcNodeId, Name = methodName, Type = NodeType.Function, FilePath = relativePath });
                    edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                }
                continue;
            }

            // attr_accessor / attr_reader / attr_writer
            var attrMatch = AttrRegex.Match(line);
            if (attrMatch.Success && currentType is not null)
            {
                var attrs = attrMatch.Groups[1].Value;
                foreach (var match in Regex.Matches(attrs, @":(\w+)").Cast<Match>())
                {
                    symbols.Add(new SymbolInfo { Name = match.Groups[1].Value, Kind = SymbolKind.Property, FilePath = relativePath, Line = lineNum, ParentSymbol = currentType });
                }
                continue;
            }

            // CONSTANT = ...
            var constMatch = ConstantRegex.Match(line);
            if (constMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = constMatch.Groups[1].Value, Kind = SymbolKind.Variable, FilePath = relativePath, Line = lineNum, ParentSymbol = currentType });
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static void ResolveRequire(
        List<GraphEdge> edges, string sourceModuleId, string reqPath,
        HashSet<string> allRelativePaths)
    {
        // require 'lib/foo' -> lib/foo.rb
        var filePath = reqPath.Replace("::", "/");
        if (!filePath.EndsWith(".rb")) filePath += ".rb";

        foreach (var rp in allRelativePaths)
        {
            if (rp.EndsWith(filePath, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(new GraphEdge { Source = sourceModuleId, Target = $"module:{StripExtension(rp)}", Relationship = EdgeRelationship.Imports });
                return;
            }
        }
    }

    private static void ResolveRequireRelative(
        List<GraphEdge> edges, string sourceModuleId, string reqPath,
        string currentFile, HashSet<string> allRelativePaths)
    {
        var dir = Path.GetDirectoryName(currentFile)?.Replace('\\', '/') ?? "";
        var candidate = string.IsNullOrEmpty(dir) ? reqPath : $"{dir}/{reqPath}";
        if (!candidate.EndsWith(".rb")) candidate += ".rb";

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
                if (Extensions.Contains(Path.GetExtension(file)))
                    result.Add(file);
            }
        }
        catch (UnauthorizedAccessException) { }
    }

    private record CombinedResult(List<SymbolInfo> Symbols, List<GraphNode> Nodes, List<GraphEdge> Edges);
    private record FileParseResult(List<SymbolInfo> Symbols, List<GraphNode> Nodes, List<GraphEdge> Edges);
}
