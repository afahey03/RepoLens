using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Rust source files using regex-based text analysis.
/// Extracts use/mod declarations, structs, enums, traits, impl blocks,
/// functions, type aliases, and constants. Builds dependency graph
/// nodes and edges for containment, imports, and trait implementation.
/// </summary>
public class RustParser : ILanguageParser
{
    private readonly ILogger<RustParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "target", ".cargo"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".rs" };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex UseRegex = new(
        @"^\s*(?:pub\s+)?use\s+([\w:]+)(?:::\{.*?\}|::\*)?;",
        RegexOptions.Compiled);

    private static readonly Regex ModRegex = new(
        @"^\s*(?:pub\s+)?mod\s+(\w+)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex ModBlockRegex = new(
        @"^\s*(?:pub\s+)?mod\s+(\w+)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex StructRegex = new(
        @"^\s*(?:pub(?:\([\w:]+\))?\s+)?struct\s+(\w+)(?:<[^>]+>)?",
        RegexOptions.Compiled);

    private static readonly Regex EnumRegex = new(
        @"^\s*(?:pub(?:\([\w:]+\))?\s+)?enum\s+(\w+)(?:<[^>]+>)?",
        RegexOptions.Compiled);

    private static readonly Regex TraitRegex = new(
        @"^\s*(?:pub(?:\([\w:]+\))?\s+)?trait\s+(\w+)(?:<[^>]+>)?(?:\s*:\s*(.+?))?\s*\{",
        RegexOptions.Compiled);

    /// <summary>impl Trait for Type / impl Type</summary>
    private static readonly Regex ImplRegex = new(
        @"^\s*impl(?:<[^>]+>)?\s+(\w+)(?:<[^>]+>)?\s+for\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex ImplSelfRegex = new(
        @"^\s*impl(?:<[^>]+>)?\s+(\w+)(?:<[^>]+>)?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex FnRegex = new(
        @"^\s*(?:pub(?:\([\w:]+\))?\s+)?(?:async\s+)?(?:unsafe\s+)?(?:const\s+)?fn\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex TypeAliasRegex = new(
        @"^\s*(?:pub(?:\([\w:]+\))?\s+)?type\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex ConstStaticRegex = new(
        @"^\s*(?:pub(?:\([\w:]+\))?\s+)?(?:const|static)\s+(\w+)\s*:",
        RegexOptions.Compiled);

    private static readonly Regex MacroDefRegex = new(
        @"^\s*(?:pub\s+)?macro_rules!\s+(\w+)",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Rust" };

    public RustParser(ILogger<RustParser> logger) => _logger = logger;

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
        _logger.LogDebug("RustParser: found {Count} .rs files", files.Count);

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

        _logger.LogInformation("RustParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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

        string? currentImplType = null;
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

            // use
            var useMatch = UseRegex.Match(line);
            if (useMatch.Success)
            {
                var path = useMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = path, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveModImport(edges, moduleId, path, allRelativePaths);
                continue;
            }

            // mod declaration (file reference)
            var modMatch = ModRegex.Match(line);
            if (modMatch.Success)
            {
                var modName = modMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = modName, Kind = SymbolKind.Module, FilePath = relativePath, Line = lineNum });
                ResolveModDecl(edges, moduleId, modName, relativePath, allRelativePaths);
                continue;
            }

            // mod block
            var modBlockMatch = ModBlockRegex.Match(line);
            if (modBlockMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = modBlockMatch.Groups[1].Value, Kind = SymbolKind.Module, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // impl Trait for Type
            var implMatch = ImplRegex.Match(line);
            if (implMatch.Success)
            {
                var traitName = implMatch.Groups[1].Value;
                var typeName = implMatch.Groups[2].Value;
                currentImplType = typeName;
                edges.Add(new GraphEdge { Source = $"class:{typeName}", Target = $"interface:{traitName}", Relationship = EdgeRelationship.Implements });
                continue;
            }

            // impl Type
            var implSelfMatch = ImplSelfRegex.Match(line);
            if (implSelfMatch.Success && !TraitRegex.IsMatch(line) && !StructRegex.IsMatch(line) && !EnumRegex.IsMatch(line))
            {
                currentImplType = implSelfMatch.Groups[1].Value;
                continue;
            }

            // trait
            var traitMatch = TraitRegex.Match(line);
            if (traitMatch.Success)
            {
                var name = traitMatch.Groups[1].Value;
                var nodeId = $"interface:{StripExtension(relativePath)}.{name}";

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Interface, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Interface, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                if (traitMatch.Groups[2].Success)
                {
                    foreach (var superTrait in traitMatch.Groups[2].Value.Split('+', StringSplitOptions.TrimEntries))
                    {
                        if (!string.IsNullOrEmpty(superTrait))
                            edges.Add(new GraphEdge { Source = nodeId, Target = $"interface:{superTrait.Trim()}", Relationship = EdgeRelationship.Inherits });
                    }
                }
                currentImplType = null;
                continue;
            }

            // struct
            var structMatch = StructRegex.Match(line);
            if (structMatch.Success)
            {
                var name = structMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["rustKind"] = "struct" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                currentImplType = null;
                continue;
            }

            // enum
            var enumMatch = EnumRegex.Match(line);
            if (enumMatch.Success)
            {
                var name = enumMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath, Metadata = new() { ["rustKind"] = "enum" } });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                currentImplType = null;
                continue;
            }

            // type alias
            var typeMatch = TypeAliasRegex.Match(line);
            if (typeMatch.Success && !trimmed.StartsWith("impl"))
            {
                symbols.Add(new SymbolInfo { Name = typeMatch.Groups[1].Value, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // fn
            var fnMatch = FnRegex.Match(line);
            if (fnMatch.Success)
            {
                var fnName = fnMatch.Groups[1].Value;
                if (currentImplType is not null)
                {
                    symbols.Add(new SymbolInfo { Name = fnName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = currentImplType });
                }
                else
                {
                    var funcNodeId = $"func:{StripExtension(relativePath)}.{fnName}";
                    symbols.Add(new SymbolInfo { Name = fnName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                    nodes.Add(new GraphNode { Id = funcNodeId, Name = fnName, Type = NodeType.Function, FilePath = relativePath });
                    edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                }
                continue;
            }

            // const/static
            var constMatch = ConstStaticRegex.Match(line);
            if (constMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = constMatch.Groups[1].Value, Kind = SymbolKind.Variable, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // macro_rules!
            var macroMatch = MacroDefRegex.Match(line);
            if (macroMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = macroMatch.Groups[1].Value, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static void ResolveModImport(
        List<GraphEdge> edges, string sourceModuleId, string usePath, HashSet<string> allRelativePaths)
    {
        // Convert crate::foo::bar to src/foo/bar.rs or src/foo/bar/mod.rs
        var parts = usePath.Replace("crate::", "").Replace("::", "/");
        foreach (var rp in allRelativePaths)
        {
            var stripped = StripExtension(rp);
            if (stripped.EndsWith(parts, StringComparison.OrdinalIgnoreCase) ||
                stripped.EndsWith(parts + "/mod", StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(new GraphEdge { Source = sourceModuleId, Target = $"module:{StripExtension(rp)}", Relationship = EdgeRelationship.Imports });
                return;
            }
        }
    }

    private static void ResolveModDecl(
        List<GraphEdge> edges, string sourceModuleId, string modName,
        string currentFile, HashSet<string> allRelativePaths)
    {
        var dir = Path.GetDirectoryName(currentFile)?.Replace('\\', '/') ?? "";
        var candidate1 = string.IsNullOrEmpty(dir) ? $"{modName}.rs" : $"{dir}/{modName}.rs";
        var candidate2 = string.IsNullOrEmpty(dir) ? $"{modName}/mod.rs" : $"{dir}/{modName}/mod.rs";

        foreach (var rp in allRelativePaths)
        {
            if (rp.Equals(candidate1, StringComparison.OrdinalIgnoreCase) ||
                rp.Equals(candidate2, StringComparison.OrdinalIgnoreCase))
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
