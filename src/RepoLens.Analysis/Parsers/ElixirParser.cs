using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Elixir source files using regex-based text analysis.
/// Extracts alias/import/use/require directives, defmodule,
/// defstruct, defprotocol, defimpl, def/defp/defmacro functions,
/// module attributes, and @behaviour/@callback references.
/// </summary>
public class ElixirParser : ILanguageParser
{
    private readonly ILogger<ElixirParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "_build", "deps", ".elixir_ls", "cover", "priv/static"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ex", ".exs"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex DefmoduleRegex = new(
        @"^\s*defmodule\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex AliasRegex = new(
        @"^\s*alias\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex ImportRegex = new(
        @"^\s*import\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex UseRegex = new(
        @"^\s*use\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex RequireRegex = new(
        @"^\s*require\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex DefRegex = new(
        @"^\s*(?:def|defp)\s+(\w+[?!]?)\s*[\(]?",
        RegexOptions.Compiled);

    private static readonly Regex DefmacroRegex = new(
        @"^\s*defmacro(?:p)?\s+(\w+[?!]?)",
        RegexOptions.Compiled);

    private static readonly Regex DefstructRegex = new(
        @"^\s*defstruct\b",
        RegexOptions.Compiled);

    private static readonly Regex DefprotocolRegex = new(
        @"^\s*defprotocol\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex DefimplRegex = new(
        @"^\s*defimpl\s+([\w.]+)\s*,\s*for:\s*([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex BehaviourRegex = new(
        @"^\s*@behaviour\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex ModuleAttrRegex = new(
        @"^\s*@(\w+)\s+",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Elixir" };

    public ElixirParser(ILogger<ElixirParser> logger) => _logger = logger;

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
        _logger.LogDebug("ElixirParser: found {Count} Elixir files", files.Count);

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

        _logger.LogInformation("ElixirParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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

        string? currentModule = null;
        string? currentModuleNodeId = null;
        var knownFuncs = new HashSet<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('#')) continue;

            // defmodule
            var defmodMatch = DefmoduleRegex.Match(line);
            if (defmodMatch.Success)
            {
                var fullName = defmodMatch.Groups[1].Value;
                var simpleName = fullName.Split('.').Last();
                currentModule = simpleName;
                currentModuleNodeId = $"class:{StripExtension(relativePath)}.{simpleName}";

                symbols.Add(new SymbolInfo { Name = fullName, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = currentModuleNodeId, Name = simpleName, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = currentModuleNodeId, Relationship = EdgeRelationship.Contains });
                knownFuncs.Clear();
                continue;
            }

            // defprotocol
            var protoMatch = DefprotocolRegex.Match(line);
            if (protoMatch.Success)
            {
                var fullName = protoMatch.Groups[1].Value;
                var simpleName = fullName.Split('.').Last();
                var nodeId = $"interface:{StripExtension(relativePath)}.{simpleName}";

                symbols.Add(new SymbolInfo { Name = fullName, Kind = SymbolKind.Interface, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = simpleName, Type = NodeType.Interface, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                currentModule = simpleName;
                currentModuleNodeId = nodeId;
                knownFuncs.Clear();
                continue;
            }

            // defimpl Protocol, for: Type
            var implMatch = DefimplRegex.Match(line);
            if (implMatch.Success)
            {
                var protoName = implMatch.Groups[1].Value.Split('.').Last();
                var forType = implMatch.Groups[2].Value.Split('.').Last();
                edges.Add(new GraphEdge { Source = $"class:{forType}", Target = $"interface:{protoName}", Relationship = EdgeRelationship.Implements });
                continue;
            }

            // @behaviour
            var behMatch = BehaviourRegex.Match(line);
            if (behMatch.Success && currentModuleNodeId is not null)
            {
                var behName = behMatch.Groups[1].Value.Split('.').Last();
                edges.Add(new GraphEdge { Source = currentModuleNodeId, Target = $"interface:{behName}", Relationship = EdgeRelationship.Implements });
                continue;
            }

            // alias
            var aliasMatch = AliasRegex.Match(line);
            if (aliasMatch.Success)
            {
                var aliasedModule = aliasMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = aliasedModule, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveAlias(edges, moduleId, aliasedModule, allRelativePaths);
                continue;
            }

            // import
            var importMatch = ImportRegex.Match(line);
            if (importMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = importMatch.Groups[1].Value, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // use
            var useMatch = UseRegex.Match(line);
            if (useMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = useMatch.Groups[1].Value, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // require
            var reqMatch = RequireRegex.Match(line);
            if (reqMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = reqMatch.Groups[1].Value, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // defstruct
            if (DefstructRegex.IsMatch(line) && currentModule is not null)
            {
                symbols.Add(new SymbolInfo { Name = $"{currentModule}.__struct__", Kind = SymbolKind.Property, FilePath = relativePath, Line = lineNum, ParentSymbol = currentModule });
                continue;
            }

            // def / defp
            var defMatch = DefRegex.Match(line);
            if (defMatch.Success)
            {
                var funcName = defMatch.Groups[1].Value;
                if (currentModule is not null && !knownFuncs.Contains(funcName))
                {
                    symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = currentModule });
                    knownFuncs.Add(funcName);
                }
                else if (currentModule is null)
                {
                    var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
                    symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                    nodes.Add(new GraphNode { Id = funcNodeId, Name = funcName, Type = NodeType.Function, FilePath = relativePath });
                    edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                }
                continue;
            }

            // defmacro / defmacrop
            var macroMatch = DefmacroRegex.Match(line);
            if (macroMatch.Success && currentModule is not null)
            {
                var macroName = macroMatch.Groups[1].Value;
                if (!knownFuncs.Contains(macroName))
                {
                    symbols.Add(new SymbolInfo { Name = macroName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum, ParentSymbol = currentModule });
                    knownFuncs.Add(macroName);
                }
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static void ResolveAlias(
        List<GraphEdge> edges, string sourceModuleId, string aliasedModule,
        HashSet<string> allRelativePaths)
    {
        // MyApp.Accounts -> lib/my_app/accounts.ex
        // Convert CamelCase module to snake_case path
        var parts = aliasedModule.Split('.');
        var pathParts = parts.Select(p => ToSnakeCase(p));
        var filePath = string.Join('/', pathParts) + ".ex";

        foreach (var rp in allRelativePaths)
        {
            if (rp.EndsWith(filePath, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(new GraphEdge { Source = sourceModuleId, Target = $"module:{StripExtension(rp)}", Relationship = EdgeRelationship.Imports });
                return;
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) result.Append('_');
                result.Append(char.ToLower(c));
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
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
