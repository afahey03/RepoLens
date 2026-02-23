using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses SQL source files using regex-based text analysis.
/// Extracts CREATE TABLE/VIEW/INDEX/FUNCTION/PROCEDURE/TRIGGER
/// declarations and references. SQL has no module imports, so
/// dependencies are derived from table references within views,
/// procedures, etc.
/// </summary>
public class SqlParser : ILanguageParser
{
    private readonly ILogger<SqlParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "migrations", "__pycache__"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".sql" };

    // ─── Regex patterns (case-insensitive SQL) ─────────────────────────

    private static readonly Regex CreateTableRegex = new(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:TEMP(?:ORARY)?\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:[\w""]+\.)?[""']?(\w+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CreateViewRegex = new(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:MATERIALIZED\s+)?VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:[\w""]+\.)?[""']?(\w+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CreateIndexRegex = new(
        @"^\s*CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:[\w""]+\.)?[""']?(\w+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CreateFunctionRegex = new(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:[\w""]+\.)?[""']?(\w+)[""']?\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CreateProcRegex = new(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?PROC(?:EDURE)?\s+(?:[\w""]+\.)?[""']?(\w+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CreateTriggerRegex = new(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?TRIGGER\s+(?:[\w""]+\.)?[""']?(\w+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CreateTypeRegex = new(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?TYPE\s+(?:[\w""]+\.)?[""']?(\w+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AlterTableRegex = new(
        @"^\s*ALTER\s+TABLE\s+(?:[\w""]+\.)?[""']?(\w+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SQL" };

    public SqlParser(ILogger<SqlParser> logger) => _logger = logger;

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
        _logger.LogDebug("SqlParser: found {Count} .sql files", files.Count);

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

        _logger.LogInformation("SqlParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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
            if (trimmed.StartsWith("--")) continue;

            // CREATE TABLE
            var tableMatch = CreateTableRegex.Match(line);
            if (tableMatch.Success)
            {
                var name = tableMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["sqlKind"] = "table" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // CREATE VIEW
            var viewMatch = CreateViewRegex.Match(line);
            if (viewMatch.Success)
            {
                var name = viewMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["sqlKind"] = "view" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // CREATE INDEX
            var indexMatch = CreateIndexRegex.Match(line);
            if (indexMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = indexMatch.Groups[1].Value, Kind = SymbolKind.Variable, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // CREATE FUNCTION
            var funcMatch = CreateFunctionRegex.Match(line);
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[1].Value;
                var nodeId = $"func:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Function, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // CREATE PROCEDURE
            var procMatch = CreateProcRegex.Match(line);
            if (procMatch.Success)
            {
                var name = procMatch.Groups[1].Value;
                var nodeId = $"func:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Function, FilePath = relativePath, Metadata = new() { ["sqlKind"] = "procedure" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // CREATE TRIGGER
            var triggerMatch = CreateTriggerRegex.Match(line);
            if (triggerMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = triggerMatch.Groups[1].Value, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // CREATE TYPE
            var typeMatch = CreateTypeRegex.Match(line);
            if (typeMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = typeMatch.Groups[1].Value, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // ALTER TABLE (for reference tracking)
            var alterMatch = AlterTableRegex.Match(line);
            if (alterMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = alterMatch.Groups[1].Value, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
            }
        }

        return new FileParseResult(symbols, nodes, edges);
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
