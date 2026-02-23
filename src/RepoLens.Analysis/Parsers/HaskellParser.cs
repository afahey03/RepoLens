using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Haskell source files using regex-based text analysis.
/// Extracts module declarations, import statements, data/newtype/
/// type aliases, class/instance definitions, and top-level functions.
/// </summary>
public class HaskellParser : ILanguageParser
{
    private readonly ILogger<HaskellParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        ".stack-work", "dist-newstyle", ".cabal-sandbox"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".hs", ".lhs"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex ModuleRegex = new(
        @"^\s*module\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex ImportRegex = new(
        @"^\s*import\s+(?:qualified\s+)?([\w.]+)",
        RegexOptions.Compiled);

    // data TypeName = ... | deriving (...)
    private static readonly Regex DataRegex = new(
        @"^\s*data\s+(\w+)",
        RegexOptions.Compiled);

    // newtype TypeName = ...
    private static readonly Regex NewtypeRegex = new(
        @"^\s*newtype\s+(\w+)",
        RegexOptions.Compiled);

    // type TypeName = ...
    private static readonly Regex TypeAliasRegex = new(
        @"^\s*type\s+(\w+)",
        RegexOptions.Compiled);

    // class ClassName a where
    private static readonly Regex ClassRegex = new(
        @"^\s*class\s+(?:.*=>\s*)?(\w+)\s+",
        RegexOptions.Compiled);

    // instance ClassName TypeName where
    private static readonly Regex InstanceRegex = new(
        @"^\s*instance\s+(?:.*=>\s*)?(\w+)\s+(\w+)",
        RegexOptions.Compiled);

    // Top-level type signature: funcName :: ...
    private static readonly Regex TypeSigRegex = new(
        @"^(\w+)\s*::\s*(.+)",
        RegexOptions.Compiled);

    // Top-level function definition: funcName arg1 arg2 = ...
    private static readonly Regex FuncDefRegex = new(
        @"^(\w+)\s+(?!::)[\w_()@~]\S*.*\s*=",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Haskell" };

    public HaskellParser(ILogger<HaskellParser> logger) => _logger = logger;

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
        _logger.LogDebug("HaskellParser: found {Count} Haskell files", files.Count);

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

        _logger.LogInformation("HaskellParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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
        var knownTypeSigs = new HashSet<string>();
        var knownFuncDefs = new HashSet<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;

            // Handle literate Haskell (.lhs): only code lines start with >
            if (relativePath.EndsWith(".lhs", StringComparison.OrdinalIgnoreCase))
            {
                if (!line.StartsWith('>')) continue;
                line = line[1..];
            }

            var trimmed = line.TrimStart();

            // Block comments {- ... -}
            if (inBlockComment)
            {
                if (trimmed.Contains("-}")) inBlockComment = false;
                continue;
            }
            if (trimmed.StartsWith("{-"))
            {
                if (!trimmed.Contains("-}")) inBlockComment = true;
                continue;
            }
            if (trimmed.StartsWith("--")) continue;
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // module
            var modMatch = ModuleRegex.Match(line);
            if (modMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = modMatch.Groups[1].Value, Kind = SymbolKind.Namespace, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // import
            var importMatch = ImportRegex.Match(line);
            if (importMatch.Success)
            {
                var importedModule = importMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = importedModule, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveImport(edges, moduleId, importedModule, allRelativePaths);
                continue;
            }

            // data
            var dataMatch = DataRegex.Match(line);
            if (dataMatch.Success)
            {
                var name = dataMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // newtype
            var newtypeMatch = NewtypeRegex.Match(line);
            if (newtypeMatch.Success)
            {
                var name = newtypeMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // type alias
            var typeMatch = TypeAliasRegex.Match(line);
            if (typeMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = typeMatch.Groups[1].Value, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // class (typeclass)
            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups[1].Value;
                var nodeId = $"interface:{StripExtension(relativePath)}.{name}";
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Interface, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Interface, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // instance
            var instMatch = InstanceRegex.Match(line);
            if (instMatch.Success)
            {
                var className = instMatch.Groups[1].Value;
                var typeName = instMatch.Groups[2].Value;
                edges.Add(new GraphEdge { Source = $"class:{typeName}", Target = $"interface:{className}", Relationship = EdgeRelationship.Implements });
                continue;
            }

            // Type signature: funcName :: Type
            var sigMatch = TypeSigRegex.Match(line);
            if (sigMatch.Success && !char.IsUpper(sigMatch.Groups[1].Value[0]))
            {
                var funcName = sigMatch.Groups[1].Value;
                if (!IsKeyword(funcName))
                {
                    knownTypeSigs.Add(funcName);
                    if (!knownFuncDefs.Contains(funcName))
                    {
                        var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
                        symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                        nodes.Add(new GraphNode { Id = funcNodeId, Name = funcName, Type = NodeType.Function, FilePath = relativePath });
                        edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                        knownFuncDefs.Add(funcName);
                    }
                }
                continue;
            }

            // Function definition (only if at column 0 — top-level)
            if (line.Length > 0 && char.IsLower(line[0]))
            {
                var funcDefMatch = FuncDefRegex.Match(line);
                if (funcDefMatch.Success)
                {
                    var funcName = funcDefMatch.Groups[1].Value;
                    if (!IsKeyword(funcName) && !knownFuncDefs.Contains(funcName))
                    {
                        var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
                        symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                        nodes.Add(new GraphNode { Id = funcNodeId, Name = funcName, Type = NodeType.Function, FilePath = relativePath });
                        edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                        knownFuncDefs.Add(funcName);
                    }
                }
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static bool IsKeyword(string name) =>
        name is "module" or "where" or "import" or "data" or "newtype" or "type"
            or "class" or "instance" or "deriving" or "do" or "let" or "in"
            or "case" or "of" or "if" or "then" or "else" or "otherwise"
            or "forall" or "foreign" or "infix" or "infixl" or "infixr"
            or "default" or "main";

    private static void ResolveImport(
        List<GraphEdge> edges, string sourceModuleId, string importedModule,
        HashSet<string> allRelativePaths)
    {
        // Data.Map -> Data/Map.hs
        var filePath = importedModule.Replace('.', '/') + ".hs";

        foreach (var rp in allRelativePaths)
        {
            if (rp.EndsWith(filePath, StringComparison.OrdinalIgnoreCase))
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
