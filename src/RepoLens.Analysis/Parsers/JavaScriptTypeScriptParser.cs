using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses JavaScript and TypeScript files using regex-based text analysis.
/// Extracts imports, classes, functions, interfaces (TS), type aliases (TS),
/// and enums (TS). Builds dependency graph with module nodes, class/function
/// nodes, import edges, and containment edges.
/// </summary>
public class JavaScriptTypeScriptParser : ILanguageParser
{
    private readonly ILogger<JavaScriptTypeScriptParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".next", "coverage", "__pycache__",
        ".github", ".vscode", ".nuget", "build", "out"
    };

    private static readonly HashSet<string> JsTsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"
    };

    /// <summary>
    /// Extension resolution order for relative import paths.
    /// </summary>
    private static readonly string[] ImportResolutionSuffixes =
    [
        ".ts", ".tsx", ".js", ".jsx",
        "/index.ts", "/index.tsx", "/index.js", "/index.jsx"
    ];

    /// <summary>
    /// Max file size to attempt parsing (1 MB).
    /// </summary>
    private const long MaxParseFileSize = 1 * 1024 * 1024;

    // ─── Regex patterns ────────────────────────────────────────────────

    /// <summary>
    /// import X from 'Y'; import { X } from 'Y'; import * as X from 'Y';
    /// </summary>
    private static readonly Regex ImportFromRegex = new(
        @"^\s*import\s+.*?\s+from\s+['""](.+?)['""]",
        RegexOptions.Compiled);

    /// <summary>
    /// import 'side-effect';  (no from keyword)
    /// </summary>
    private static readonly Regex ImportSideEffectRegex = new(
        @"^\s*import\s+['""](.+?)['""]",
        RegexOptions.Compiled);

    /// <summary>
    /// const/let/var X = require('Y')
    /// </summary>
    private static readonly Regex RequireRegex = new(
        @"(?:const|let|var)\s+(\w+)\s*=\s*require\s*\(\s*['""](.+?)['""]\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// [export] [default] [abstract] class Name [extends Base] [implements I, J]
    /// </summary>
    private static readonly Regex ClassRegex = new(
        @"(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+(\w+)" +
        @"(?:\s+extends\s+(\w+))?" +
        @"(?:\s+implements\s+([\w,\s]+))?",
        RegexOptions.Compiled);

    /// <summary>
    /// [export] [default] [async] function name(...)
    /// </summary>
    private static readonly Regex FunctionDeclRegex = new(
        @"(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// [export] const/let/var name = [async] (...) => ...
    /// [export] const/let/var name = [async] function(...)
    /// </summary>
    private static readonly Regex ArrowOrExprRegex = new(
        @"(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s+)?(?:\([^)]*\)\s*=>|[\w]+\s*=>|function\b)",
        RegexOptions.Compiled);

    /// <summary>
    /// [export] interface Name [extends Base, ...]
    /// </summary>
    private static readonly Regex InterfaceRegex = new(
        @"(?:export\s+)?interface\s+(\w+)(?:\s+extends\s+([\w,\s]+))?",
        RegexOptions.Compiled);

    /// <summary>
    /// [export] type Name = ...
    /// </summary>
    private static readonly Regex TypeAliasRegex = new(
        @"(?:export\s+)?type\s+(\w+)\s*(?:<[^>]*>)?\s*=",
        RegexOptions.Compiled);

    /// <summary>
    /// [export] [const] enum Name { ... }
    /// </summary>
    private static readonly Regex EnumRegex = new(
        @"(?:export\s+)?(?:const\s+)?enum\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// export { X, Y, Z } or export { X as default }
    /// </summary>
    private static readonly Regex ReExportRegex = new(
        @"^\s*export\s+\{[^}]+\}\s+from\s+['""](.+?)['""]",
        RegexOptions.Compiled);

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string> { "JavaScript", "TypeScript" };

    public JavaScriptTypeScriptParser(ILogger<JavaScriptTypeScriptParser> logger)
    {
        _logger = logger;
    }

    // ─── Cache ─────────────────────────────────────────────────────────

    private string? _lastRepoPath;
    private CombinedResult? _lastResult;

    public async Task<List<SymbolInfo>> ExtractSymbolsAsync(
        string repoPath, CancellationToken cancellationToken = default)
    {
        var result = await GetResultAsync(repoPath, cancellationToken);
        return result.Symbols;
    }

    public async Task<(List<GraphNode> Nodes, List<GraphEdge> Edges)> BuildDependenciesAsync(
        string repoPath, CancellationToken cancellationToken = default)
    {
        var result = await GetResultAsync(repoPath, cancellationToken);
        return (result.Nodes, result.Edges);
    }

    private async Task<CombinedResult> GetResultAsync(string repoPath, CancellationToken ct)
    {
        if (_lastRepoPath == repoPath && _lastResult is not null)
            return _lastResult;

        _lastResult = await ParseRepositoryAsync(repoPath, ct);
        _lastRepoPath = repoPath;
        return _lastResult;
    }

    // ─── Repository-level parsing ──────────────────────────────────────

    private async Task<CombinedResult> ParseRepositoryAsync(string repoPath, CancellationToken ct)
    {
        var symbols = new List<SymbolInfo>();
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var createdNodeIds = new HashSet<string>();

        var files = FindJsTsFiles(repoPath);
        _logger.LogInformation("JS/TS parser: found {Count} files to parse", files.Count);

        // Collect all relative paths for import resolution
        var allRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            allRelativePaths.Add(
                Path.GetRelativePath(repoPath, f).Replace('\\', '/'));
        }

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            if (new System.IO.FileInfo(filePath).Length > MaxParseFileSize)
                continue;

            try
            {
                var relativePath = Path.GetRelativePath(repoPath, filePath).Replace('\\', '/');
                var fileResult = await ParseFileAsync(
                    filePath, relativePath, allRelativePaths, ct);

                symbols.AddRange(fileResult.Symbols);

                foreach (var node in fileResult.Nodes)
                {
                    if (createdNodeIds.Add(node.Id))
                        nodes.Add(node);
                }

                edges.AddRange(fileResult.Edges);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("JS/TS parser: failed to parse {File}: {Error}",
                    filePath, ex.Message);
            }
        }

        _logger.LogInformation(
            "JS/TS parser: extracted {Symbols} symbols, {Nodes} graph nodes, {Edges} edges",
            symbols.Count, nodes.Count, edges.Count);

        return new CombinedResult(symbols, nodes, edges);
    }

    // ─── File-level parsing ────────────────────────────────────────────

    private async Task<FileParseResult> ParseFileAsync(
        string filePath, string relativePath,
        HashSet<string> allRelativePaths, CancellationToken ct)
    {
        var symbols = new List<SymbolInfo>();
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        // Create module node for this file
        var moduleId = $"module:{StripExtension(relativePath)}";
        nodes.Add(new GraphNode
        {
            Id = moduleId,
            Name = Path.GetFileNameWithoutExtension(filePath),
            Type = NodeType.Module,
            FilePath = relativePath
        });

        // File → Contains → Module
        edges.Add(new GraphEdge
        {
            Source = relativePath,
            Target = moduleId,
            Relationship = EdgeRelationship.Contains
        });

        var lines = await File.ReadAllLinesAsync(filePath, ct);
        var fileDir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed.StartsWith("//") ||
                trimmed.StartsWith("/*") || trimmed.StartsWith("*"))
                continue;

            // ── Re-exports: export { ... } from '...' ────────────
            var reExportMatch = ReExportRegex.Match(line);
            if (reExportMatch.Success)
            {
                var importPath = reExportMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = importPath,
                    Kind = SymbolKind.Import,
                    FilePath = relativePath,
                    Line = lineNum
                });
                AddImportEdge(edges, moduleId, importPath, fileDir, allRelativePaths);
                continue;
            }

            // ── import X from 'Y' ────────────────────────────────
            var importMatch = ImportFromRegex.Match(line);
            if (importMatch.Success)
            {
                var importPath = importMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = importPath,
                    Kind = SymbolKind.Import,
                    FilePath = relativePath,
                    Line = lineNum
                });
                AddImportEdge(edges, moduleId, importPath, fileDir, allRelativePaths);
                continue;
            }

            // ── import 'side-effect' ─────────────────────────────
            var sideEffectMatch = ImportSideEffectRegex.Match(line);
            if (sideEffectMatch.Success && !line.Contains("from"))
            {
                symbols.Add(new SymbolInfo
                {
                    Name = sideEffectMatch.Groups[1].Value,
                    Kind = SymbolKind.Import,
                    FilePath = relativePath,
                    Line = lineNum
                });
                continue;
            }

            // ── require('Y') ─────────────────────────────────────
            var requireMatch = RequireRegex.Match(line);
            if (requireMatch.Success)
            {
                var varName = requireMatch.Groups[1].Value;
                var requirePath = requireMatch.Groups[2].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = $"{varName} ({requirePath})",
                    Kind = SymbolKind.Import,
                    FilePath = relativePath,
                    Line = lineNum
                });
                AddImportEdge(edges, moduleId, requirePath, fileDir, allRelativePaths);
                continue;
            }

            // ── class declaration ─────────────────────────────────
            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                var className = classMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = className,
                    Kind = SymbolKind.Class,
                    FilePath = relativePath,
                    Line = lineNum
                });

                var classNodeId = $"class:{StripExtension(relativePath)}.{className}";
                nodes.Add(new GraphNode
                {
                    Id = classNodeId,
                    Name = className,
                    Type = NodeType.Class,
                    FilePath = relativePath
                });

                // Module → Contains → Class
                edges.Add(new GraphEdge
                {
                    Source = moduleId,
                    Target = classNodeId,
                    Relationship = EdgeRelationship.Contains
                });

                // extends
                if (classMatch.Groups[2].Success && classMatch.Groups[2].Value.Length > 0)
                {
                    edges.Add(new GraphEdge
                    {
                        Source = classNodeId,
                        Target = $"type:{classMatch.Groups[2].Value.Trim()}",
                        Relationship = EdgeRelationship.Inherits
                    });
                }

                // implements
                if (classMatch.Groups[3].Success && classMatch.Groups[3].Value.Length > 0)
                {
                    foreach (var iface in classMatch.Groups[3].Value.Split(','))
                    {
                        var name = iface.Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            edges.Add(new GraphEdge
                            {
                                Source = classNodeId,
                                Target = $"type:{name}",
                                Relationship = EdgeRelationship.Implements
                            });
                        }
                    }
                }
                continue;
            }

            // ── interface (TypeScript) ────────────────────────────
            var ifaceMatch = InterfaceRegex.Match(line);
            if (ifaceMatch.Success)
            {
                var ifaceName = ifaceMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = ifaceName,
                    Kind = SymbolKind.Interface,
                    FilePath = relativePath,
                    Line = lineNum
                });

                var ifaceNodeId = $"interface:{StripExtension(relativePath)}.{ifaceName}";
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

            // ── function declaration ──────────────────────────────
            var funcMatch = FunctionDeclRegex.Match(line);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = funcName,
                    Kind = SymbolKind.Function,
                    FilePath = relativePath,
                    Line = lineNum
                });

                var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
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

            // ── arrow function / function expression ──────────────
            var arrowMatch = ArrowOrExprRegex.Match(line);
            if (arrowMatch.Success)
            {
                var funcName = arrowMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = funcName,
                    Kind = SymbolKind.Function,
                    FilePath = relativePath,
                    Line = lineNum
                });

                var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
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

            // ── type alias (TypeScript) ───────────────────────────
            var typeMatch = TypeAliasRegex.Match(line);
            if (typeMatch.Success)
            {
                symbols.Add(new SymbolInfo
                {
                    Name = typeMatch.Groups[1].Value,
                    Kind = SymbolKind.Class,
                    FilePath = relativePath,
                    Line = lineNum
                });
                continue;
            }

            // ── enum (TypeScript) ─────────────────────────────────
            var enumMatch = EnumRegex.Match(line);
            if (enumMatch.Success)
            {
                symbols.Add(new SymbolInfo
                {
                    Name = enumMatch.Groups[1].Value,
                    Kind = SymbolKind.Class,
                    FilePath = relativePath,
                    Line = lineNum
                });
                continue;
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    // ─── Import resolution ─────────────────────────────────────────────

    /// <summary>
    /// Resolves a relative import path and adds an edge to the graph.
    /// External (npm) packages are ignored.
    /// </summary>
    private static void AddImportEdge(
        List<GraphEdge> edges, string sourceModuleId,
        string importPath, string currentFileDir,
        HashSet<string> allRelativePaths)
    {
        var resolved = ResolveImportPath(importPath, currentFileDir, allRelativePaths);
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
    /// Resolves a relative import path to an actual file in the repo.
    /// Returns null for external/npm packages.
    /// </summary>
    private static string? ResolveImportPath(
        string importPath, string currentFileDir,
        HashSet<string> allRelativePaths)
    {
        // External package — not a relative path
        if (!importPath.StartsWith('.') && !importPath.StartsWith('/'))
            return null;

        var resolved = NormalizePath(
            string.IsNullOrEmpty(currentFileDir)
                ? importPath
                : $"{currentFileDir}/{importPath}");

        // Exact match (rare — usually imports omit extensions)
        if (allRelativePaths.Contains(resolved))
            return resolved;

        // Try standard extension/index suffixes
        foreach (var suffix in ImportResolutionSuffixes)
        {
            var candidate = resolved + suffix;
            if (allRelativePaths.Contains(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Normalizes a path by resolving '.' and '..' segments.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var parts = path.Split('/');
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == "." || string.IsNullOrEmpty(part)) continue;
            if (part == ".." && stack.Count > 0)
                stack.RemoveAt(stack.Count - 1);
            else if (part != "..")
                stack.Add(part);
        }
        return string.Join('/', stack);
    }

    private static string StripExtension(string path)
    {
        var lastDot = path.LastIndexOf('.');
        var lastSlash = path.LastIndexOf('/');
        return lastDot > lastSlash ? path[..lastDot] : path;
    }

    // ─── File discovery ────────────────────────────────────────────────

    private static List<string> FindJsTsFiles(string rootPath)
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
                if (JsTsExtensions.Contains(ext))
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
