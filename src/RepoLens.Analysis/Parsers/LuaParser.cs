using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Lua source files using regex-based text analysis.
/// Extracts require calls, module definitions, functions (local,
/// module-level, and method-style), and local variables.
/// </summary>
public class LuaParser : ILanguageParser
{
    private readonly ILogger<LuaParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        ".luarocks", "lua_modules"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lua"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex RequireRegex = new(
        @"(?:local\s+\w+\s*=\s*)?require\s*\(?['""]([^'""]+)['""]\)?",
        RegexOptions.Compiled);

    private static readonly Regex ModuleRegex = new(
        @"^\s*module\s*\(\s*['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    // function ModName.funcName(...)  or  function ModName:methodName(...)
    private static readonly Regex ModuleFuncRegex = new(
        @"^\s*function\s+(\w+)[.:]+(\w+)\s*\(",
        RegexOptions.Compiled);

    // function funcName(...)
    private static readonly Regex GlobalFuncRegex = new(
        @"^\s*function\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    // local function funcName(...)
    private static readonly Regex LocalFuncRegex = new(
        @"^\s*local\s+function\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    // local funcName = function(...)
    private static readonly Regex LocalFuncVarRegex = new(
        @"^\s*local\s+(\w+)\s*=\s*function\s*\(",
        RegexOptions.Compiled);

    // M.funcName = function(...)
    private static readonly Regex AssignFuncRegex = new(
        @"^\s*(\w+)\.(\w+)\s*=\s*function\s*\(",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Lua" };

    public LuaParser(ILogger<LuaParser> logger) => _logger = logger;

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
        _logger.LogDebug("LuaParser: found {Count} Lua files", files.Count);

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

        _logger.LogInformation("LuaParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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

        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            // Block comments: --[[ ... ]]
            if (inBlockComment)
            {
                if (line.Contains("]]")) inBlockComment = false;
                continue;
            }
            if (trimmed.StartsWith("--[["))
            {
                if (!line.Contains("]]")) inBlockComment = true;
                continue;
            }
            if (trimmed.StartsWith("--")) continue;

            // require
            var reqMatch = RequireRegex.Match(line);
            if (reqMatch.Success)
            {
                var reqPath = reqMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = reqPath, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveRequire(edges, moduleId, reqPath, allRelativePaths);
                continue;
            }

            // module("name")
            var modMatch = ModuleRegex.Match(line);
            if (modMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = modMatch.Groups[1].Value, Kind = SymbolKind.Namespace, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // local function
            var localFuncMatch = LocalFuncRegex.Match(line);
            if (localFuncMatch.Success)
            {
                var name = localFuncMatch.Groups[1].Value;
                var funcNodeId = $"func:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = funcNodeId, Name = name, Type = NodeType.Function, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // local name = function
            var localFuncVarMatch = LocalFuncVarRegex.Match(line);
            if (localFuncVarMatch.Success)
            {
                var name = localFuncVarMatch.Groups[1].Value;
                var funcNodeId = $"func:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = funcNodeId, Name = name, Type = NodeType.Function, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // Module.func or Module:method
            var modFuncMatch = ModuleFuncRegex.Match(line);
            if (modFuncMatch.Success)
            {
                var parentName = modFuncMatch.Groups[1].Value;
                var methodName = modFuncMatch.Groups[2].Value;
                symbols.Add(new SymbolInfo { Name = methodName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = parentName });
                continue;
            }

            // M.funcName = function
            var assignFuncMatch = AssignFuncRegex.Match(line);
            if (assignFuncMatch.Success)
            {
                var parentName = assignFuncMatch.Groups[1].Value;
                var methodName = assignFuncMatch.Groups[2].Value;
                symbols.Add(new SymbolInfo { Name = methodName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = parentName });
                continue;
            }

            // function funcName (global)
            var globalFuncMatch = GlobalFuncRegex.Match(line);
            if (globalFuncMatch.Success)
            {
                var name = globalFuncMatch.Groups[1].Value;
                var funcNodeId = $"func:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = funcNodeId, Name = name, Type = NodeType.Function, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static void ResolveRequire(
        List<GraphEdge> edges, string sourceModuleId, string reqPath,
        HashSet<string> allRelativePaths)
    {
        // Lua require("a.b.c") maps to a/b/c.lua or a/b/c/init.lua
        var filePath = reqPath.Replace('.', '/');
        var candidates = new[] { $"{filePath}.lua", $"{filePath}/init.lua" };

        foreach (var candidate in candidates)
        {
            foreach (var rp in allRelativePaths)
            {
                if (rp.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    edges.Add(new GraphEdge { Source = sourceModuleId, Target = $"module:{StripExtension(rp)}", Relationship = EdgeRelationship.Imports });
                    return;
                }
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
