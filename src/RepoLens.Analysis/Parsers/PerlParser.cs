using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Perl source files using regex-based text analysis.
/// Extracts use/require statements, packages, subroutines,
/// constants, and inheritance (@ISA / use parent / use base).
/// </summary>
public class PerlParser : ILanguageParser
{
    private readonly ILogger<PerlParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "blib", "_build", "local", "fatlib"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pl", ".pm", ".t"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex UseRegex = new(
        @"^\s*use\s+([\w:]+)",
        RegexOptions.Compiled);

    private static readonly Regex RequireRegex = new(
        @"^\s*require\s+['""]?([^'"";\s]+)",
        RegexOptions.Compiled);

    private static readonly Regex PackageRegex = new(
        @"^\s*package\s+([\w:]+)",
        RegexOptions.Compiled);

    private static readonly Regex SubRegex = new(
        @"^\s*sub\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex UseParentRegex = new(
        @"^\s*use\s+(?:parent|base)\s+(?:-norequire\s+)?['""]?([\w:]+)",
        RegexOptions.Compiled);

    private static readonly Regex IsaRegex = new(
        @"@ISA\s*=\s*\(\s*(.*?)\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex ConstantRegex = new(
        @"^\s*use\s+constant\s+(\w+)\s*=>",
        RegexOptions.Compiled);

    private static readonly Regex HasRegex = new(
        @"^\s*has\s+['""]?(\w+)",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Perl" };

    public PerlParser(ILogger<PerlParser> logger) => _logger = logger;

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
        _logger.LogDebug("PerlParser: found {Count} Perl files", files.Count);

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

        _logger.LogInformation("PerlParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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

        string? currentPackage = null;
        string? currentPackageNodeId = null;
        var inPod = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            // POD documentation blocks
            if (inPod)
            {
                if (trimmed.StartsWith("=cut")) inPod = false;
                continue;
            }
            if (trimmed.StartsWith("=") && !trimmed.StartsWith("=="))
            {
                inPod = true;
                continue;
            }
            if (trimmed.StartsWith('#')) continue;
            // __END__ / __DATA__
            if (trimmed is "__END__" or "__DATA__") break;

            // package
            var pkgMatch = PackageRegex.Match(line);
            if (pkgMatch.Success)
            {
                var pkgName = pkgMatch.Groups[1].Value;
                currentPackage = pkgName.Split("::").Last();
                currentPackageNodeId = $"class:{StripExtension(relativePath)}.{currentPackage}";

                symbols.Add(new SymbolInfo { Name = pkgName, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = currentPackageNodeId, Name = currentPackage, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = currentPackageNodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // use parent / use base
            var useParentMatch = UseParentRegex.Match(line);
            if (useParentMatch.Success && currentPackageNodeId is not null)
            {
                var parentPkg = useParentMatch.Groups[1].Value.Split("::").Last();
                edges.Add(new GraphEdge { Source = currentPackageNodeId, Target = $"class:{parentPkg}", Relationship = EdgeRelationship.Inherits });
                continue;
            }

            // @ISA = (...)
            var isaMatch = IsaRegex.Match(line);
            if (isaMatch.Success && currentPackageNodeId is not null)
            {
                var parents = isaMatch.Groups[1].Value;
                foreach (Match m in Regex.Matches(parents, @"[\w:]+"))
                {
                    var parentName = m.Value.Split("::").Last();
                    edges.Add(new GraphEdge { Source = currentPackageNodeId, Target = $"class:{parentName}", Relationship = EdgeRelationship.Inherits });
                }
                continue;
            }

            // use constant
            var constMatch = ConstantRegex.Match(line);
            if (constMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = constMatch.Groups[1].Value, Kind = SymbolKind.Variable, FilePath = relativePath, Line = lineNum, ParentSymbol = currentPackage });
                continue;
            }

            // use Module
            var useMatch = UseRegex.Match(line);
            if (useMatch.Success)
            {
                var usedModule = useMatch.Groups[1].Value;
                if (usedModule is "strict" or "warnings" or "utf8" or "constant" or "parent" or "base"
                    or "Exporter" or "Carp" or "Data::Dumper" or "File::Basename" or "File::Path") continue;

                symbols.Add(new SymbolInfo { Name = usedModule, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveUse(edges, moduleId, usedModule, allRelativePaths);
                continue;
            }

            // require
            var reqMatch = RequireRegex.Match(line);
            if (reqMatch.Success)
            {
                var reqPath = reqMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = reqPath, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // has 'attr' (Moose / Moo)
            var hasMatch = HasRegex.Match(line);
            if (hasMatch.Success && currentPackage is not null)
            {
                symbols.Add(new SymbolInfo { Name = hasMatch.Groups[1].Value, Kind = SymbolKind.Property, FilePath = relativePath, Line = lineNum, ParentSymbol = currentPackage });
                continue;
            }

            // sub
            var subMatch = SubRegex.Match(line);
            if (subMatch.Success)
            {
                var subName = subMatch.Groups[1].Value;
                if (currentPackage is not null)
                {
                    symbols.Add(new SymbolInfo { Name = subName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = currentPackage });
                }
                else
                {
                    var funcNodeId = $"func:{StripExtension(relativePath)}.{subName}";
                    symbols.Add(new SymbolInfo { Name = subName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                    nodes.Add(new GraphNode { Id = funcNodeId, Name = subName, Type = NodeType.Function, FilePath = relativePath });
                    edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                }
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static void ResolveUse(
        List<GraphEdge> edges, string sourceModuleId, string usedModule,
        HashSet<string> allRelativePaths)
    {
        // use Foo::Bar -> Foo/Bar.pm
        var filePath = usedModule.Replace("::", "/") + ".pm";

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
