using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Go source files using regex-based text analysis.
/// Extracts packages, imports, structs, interfaces, functions, methods
/// (receiver functions), and type aliases. Builds dependency graph nodes
/// and edges for containment, imports, and interface implementation hints.
/// </summary>
public class GoParser : ILanguageParser
{
    private readonly ILogger<GoParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "vendor", ".cache", "testdata"
    };

    /// <summary>Max file size to attempt parsing (1 MB).</summary>
    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> GoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".go"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    /// <summary>Matches: package main</summary>
    private static readonly Regex PackageRegex = new(
        @"^\s*package\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>Matches single import: import "path"</summary>
    private static readonly Regex SingleImportRegex = new(
        @"^\s*import\s+(?:\w+\s+)?""([^""]+)""",
        RegexOptions.Compiled);

    /// <summary>Matches import inside a grouped import block: "path" or alias "path"</summary>
    private static readonly Regex GroupedImportRegex = new(
        @"^\s*(?:\w+\s+)?""([^""]+)""",
        RegexOptions.Compiled);

    /// <summary>Matches: type Name struct { ...</summary>
    private static readonly Regex StructRegex = new(
        @"^\s*type\s+(\w+)\s+struct\b",
        RegexOptions.Compiled);

    /// <summary>Matches: type Name interface { ...</summary>
    private static readonly Regex InterfaceRegex = new(
        @"^\s*type\s+(\w+)\s+interface\b",
        RegexOptions.Compiled);

    /// <summary>Matches: type Name otherType (type alias / named type)</summary>
    private static readonly Regex TypeAliasRegex = new(
        @"^\s*type\s+(\w+)\s+(?!struct\b|interface\b)(\w[\w.*]*)",
        RegexOptions.Compiled);

    /// <summary>Matches top-level function: func FuncName(</summary>
    private static readonly Regex FunctionRegex = new(
        @"^\s*func\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Matches method (receiver function): func (r *Type) MethodName(</summary>
    private static readonly Regex MethodRegex = new(
        @"^\s*func\s+\(\s*\w+\s+\*?(\w+)\s*\)\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Matches: const/var block or single declaration</summary>
    private static readonly Regex ConstVarRegex = new(
        @"^\s*(?:const|var)\s+(\w+)",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Go" };

    public GoParser(ILogger<GoParser> logger)
    {
        _logger = logger;
    }

    // ─── Cache ─────────────────────────────────────────────────────────
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

    // ─── Repository-level parsing ──────────────────────────────────────

    private Task<CombinedResult> ParseRepositoryAsync(string repoPath)
    {
        if (_lastRepoPath == repoPath && _lastResult is not null)
            return Task.FromResult(_lastResult);

        var files = FindGoFiles(repoPath);
        _logger.LogDebug("GoParser: found {Count} .go files", files.Count);

        var allSymbols = new List<SymbolInfo>();
        var allNodes = new List<GraphNode>();
        var allEdges = new List<GraphEdge>();

        // Collect all relative paths for import resolution
        var allRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(repoPath, f).Replace('\\', '/');
            allRelativePaths.Add(rel);
        }

        // Detect the module path from go.mod for import resolution
        var goModulePath = DetectModulePath(repoPath);

        foreach (var file in files)
        {
            var fi = new System.IO.FileInfo(file);
            if (fi.Length > MaxParseFileSize) continue;

            var relativePath = Path.GetRelativePath(repoPath, file).Replace('\\', '/');
            var parsed = ParseFile(relativePath, file, allRelativePaths, goModulePath);

            allSymbols.AddRange(parsed.Symbols);
            allNodes.AddRange(parsed.Nodes);
            allEdges.AddRange(parsed.Edges);
        }

        _logger.LogInformation(
            "GoParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
            allSymbols.Count, allNodes.Count, allEdges.Count);

        _lastRepoPath = repoPath;
        _lastResult = new CombinedResult(allSymbols, allNodes, allEdges);
        return Task.FromResult(_lastResult);
    }

    // ─── File-level parsing ────────────────────────────────────────────

    private static FileParseResult ParseFile(
        string relativePath, string absolutePath,
        HashSet<string> allRelativePaths, string? goModulePath)
    {
        var symbols = new List<SymbolInfo>();
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        string[] lines;
        try { lines = File.ReadAllLines(absolutePath); }
        catch { return new FileParseResult(symbols, nodes, edges); }

        // Module node for this file
        var moduleId = $"module:{StripExtension(relativePath)}";
        nodes.Add(new GraphNode
        {
            Id = moduleId,
            Name = Path.GetFileNameWithoutExtension(relativePath),
            Type = NodeType.Module,
            FilePath = relativePath
        });

        string? currentPackage = null;
        var inImportBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            // Skip comments
            if (trimmed.StartsWith("//"))
                continue;

            // ── grouped import block tracking ──
            if (trimmed.StartsWith("import ("))
            {
                inImportBlock = true;
                continue;
            }

            if (inImportBlock)
            {
                if (trimmed.StartsWith(')'))
                {
                    inImportBlock = false;
                    continue;
                }

                var groupedMatch = GroupedImportRegex.Match(trimmed);
                if (groupedMatch.Success)
                {
                    var importPath = groupedMatch.Groups[1].Value;
                    symbols.Add(new SymbolInfo
                    {
                        Name = importPath,
                        Kind = SymbolKind.Import,
                        FilePath = relativePath,
                        Line = lineNum
                    });
                    AddImportEdge(edges, moduleId, importPath, relativePath, allRelativePaths, goModulePath);
                }
                continue;
            }

            // ── package declaration ──
            var packageMatch = PackageRegex.Match(line);
            if (packageMatch.Success)
            {
                currentPackage = packageMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = currentPackage,
                    Kind = SymbolKind.Namespace,
                    FilePath = relativePath,
                    Line = lineNum
                });
                continue;
            }

            // ── single-line import ──
            var singleImportMatch = SingleImportRegex.Match(line);
            if (singleImportMatch.Success)
            {
                var importPath = singleImportMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = importPath,
                    Kind = SymbolKind.Import,
                    FilePath = relativePath,
                    Line = lineNum
                });
                AddImportEdge(edges, moduleId, importPath, relativePath, allRelativePaths, goModulePath);
                continue;
            }

            // ── type Name struct ──
            var structMatch = StructRegex.Match(line);
            if (structMatch.Success)
            {
                var structName = structMatch.Groups[1].Value;
                var structNodeId = $"class:{StripExtension(relativePath)}.{structName}";

                symbols.Add(new SymbolInfo
                {
                    Name = structName,
                    Kind = SymbolKind.Class, // Struct mapped to Class
                    FilePath = relativePath,
                    Line = lineNum
                });

                nodes.Add(new GraphNode
                {
                    Id = structNodeId,
                    Name = structName,
                    Type = NodeType.Class, // struct → Class node
                    FilePath = relativePath,
                    Metadata = new Dictionary<string, string> { ["goKind"] = "struct" }
                });

                edges.Add(new GraphEdge
                {
                    Source = moduleId,
                    Target = structNodeId,
                    Relationship = EdgeRelationship.Contains
                });
                continue;
            }

            // ── type Name interface ──
            var ifaceMatch = InterfaceRegex.Match(line);
            if (ifaceMatch.Success)
            {
                var ifaceName = ifaceMatch.Groups[1].Value;
                var ifaceNodeId = $"interface:{StripExtension(relativePath)}.{ifaceName}";

                symbols.Add(new SymbolInfo
                {
                    Name = ifaceName,
                    Kind = SymbolKind.Interface,
                    FilePath = relativePath,
                    Line = lineNum
                });

                nodes.Add(new GraphNode
                {
                    Id = ifaceNodeId,
                    Name = ifaceName,
                    Type = NodeType.Interface,
                    FilePath = relativePath
                });

                edges.Add(new GraphEdge
                {
                    Source = moduleId,
                    Target = ifaceNodeId,
                    Relationship = EdgeRelationship.Contains
                });
                continue;
            }

            // ── type alias / named type ──
            var typeAliasMatch = TypeAliasRegex.Match(line);
            if (typeAliasMatch.Success)
            {
                symbols.Add(new SymbolInfo
                {
                    Name = typeAliasMatch.Groups[1].Value,
                    Kind = SymbolKind.Class,
                    FilePath = relativePath,
                    Line = lineNum
                });
                continue;
            }

            // ── method (receiver func) ──
            var methodMatch = MethodRegex.Match(line);
            if (methodMatch.Success)
            {
                var receiverType = methodMatch.Groups[1].Value;
                var methodName = methodMatch.Groups[2].Value;

                symbols.Add(new SymbolInfo
                {
                    Name = methodName,
                    Kind = SymbolKind.Method,
                    FilePath = relativePath,
                    Line = lineNum,
                    ParentSymbol = receiverType
                });
                continue;
            }

            // ── top-level function ──
            var funcMatch = FunctionRegex.Match(line);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";

                symbols.Add(new SymbolInfo
                {
                    Name = funcName,
                    Kind = SymbolKind.Function,
                    FilePath = relativePath,
                    Line = lineNum
                });

                nodes.Add(new GraphNode
                {
                    Id = funcNodeId,
                    Name = funcName,
                    Type = NodeType.Function,
                    FilePath = relativePath
                });

                edges.Add(new GraphEdge
                {
                    Source = moduleId,
                    Target = funcNodeId,
                    Relationship = EdgeRelationship.Contains
                });
                continue;
            }

            // ── const/var declarations ──
            if (!inImportBlock)
            {
                var constVarMatch = ConstVarRegex.Match(line);
                if (constVarMatch.Success)
                {
                    symbols.Add(new SymbolInfo
                    {
                        Name = constVarMatch.Groups[1].Value,
                        Kind = SymbolKind.Variable,
                        FilePath = relativePath,
                        Line = lineNum
                    });
                }
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    // ─── Import resolution ─────────────────────────────────────────────

    /// <summary>
    /// Resolves a Go import path and adds an edge to the dependency graph.
    /// Standard library and external packages are skipped.
    /// </summary>
    private static void AddImportEdge(
        List<GraphEdge> edges, string sourceModuleId,
        string importPath, string currentFilePath,
        HashSet<string> allRelativePaths, string? goModulePath)
    {
        var resolved = ResolveImport(importPath, currentFilePath, allRelativePaths, goModulePath);
        if (resolved is null) return;

        var targetModuleId = $"module:{StripExtension(resolved)}";
        edges.Add(new GraphEdge
        {
            Source = sourceModuleId,
            Target = targetModuleId,
            Relationship = EdgeRelationship.Imports
        });
    }

    /// <summary>
    /// Resolves a Go import path to a file in the repo.
    /// Uses the module path from go.mod to identify local packages.
    /// Returns null for standard library / external packages.
    /// </summary>
    private static string? ResolveImport(
        string importPath, string currentFilePath,
        HashSet<string> allRelativePaths, string? goModulePath)
    {
        // Standard library doesn't contain dots in first segment typically
        if (!importPath.Contains('.') && !importPath.Contains('/'))
            return null;

        string localPath;

        if (goModulePath is not null && importPath.StartsWith(goModulePath))
        {
            // Strip the module prefix to get the relative package directory
            localPath = importPath[(goModulePath.Length)..].TrimStart('/');
        }
        else
        {
            // Could be a relative internal import — try as-is
            localPath = importPath;
        }

        if (string.IsNullOrEmpty(localPath))
            localPath = ".";

        // Find any .go file in that directory
        var dirPrefix = localPath + "/";
        foreach (var rp in allRelativePaths)
        {
            if (rp.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase) &&
                rp.EndsWith(".go", StringComparison.OrdinalIgnoreCase))
            {
                return rp;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the go.mod file to extract the module path.
    /// </summary>
    private static string? DetectModulePath(string repoPath)
    {
        var goModPath = Path.Combine(repoPath, "go.mod");
        if (!File.Exists(goModPath)) return null;

        try
        {
            foreach (var line in File.ReadLines(goModPath))
            {
                if (line.StartsWith("module "))
                {
                    return line["module ".Length..].Trim();
                }
            }
        }
        catch { /* ignore */ }

        return null;
    }

    private static string StripExtension(string path)
    {
        var lastDot = path.LastIndexOf('.');
        var lastSlash = path.LastIndexOf('/');
        return lastDot > lastSlash ? path[..lastDot] : path;
    }

    // ─── File discovery ────────────────────────────────────────────────

    private static List<string> FindGoFiles(string rootPath)
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
                if (IgnoredDirs.Contains(name) || name.StartsWith('.'))
                    continue;
                CollectFiles(subDir, result);
            }

            foreach (var file in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (GoExtensions.Contains(ext))
                    result.Add(file);
            }
        }
        catch (UnauthorizedAccessException) { }
    }

    // ─── Result types ──────────────────────────────────────────────────

    private record CombinedResult(
        List<SymbolInfo> Symbols, List<GraphNode> Nodes, List<GraphEdge> Edges);

    private record FileParseResult(
        List<SymbolInfo> Symbols, List<GraphNode> Nodes, List<GraphEdge> Edges);
}
