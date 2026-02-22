using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Python source files using regex-based text analysis.
/// Extracts imports, classes, functions/methods, decorators, and module-level
/// variables. Builds dependency graph nodes and edges for containment,
/// imports, and inheritance.
/// </summary>
public class PythonParser : ILanguageParser
{
    private readonly ILogger<PythonParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "__pycache__", ".mypy_cache", ".pytest_cache", ".tox",
        ".eggs", "*.egg-info", "venv", ".venv", "env", ".env",
        "site-packages", "build", "htmlcov"
    };

    /// <summary>Max file size to attempt parsing (1 MB).</summary>
    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> PythonExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyw"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    /// <summary>Matches: import module / import module as alias</summary>
    private static readonly Regex ImportRegex = new(
        @"^\s*import\s+([\w.]+)(?:\s+as\s+\w+)?",
        RegexOptions.Compiled);

    /// <summary>Matches: from module import names</summary>
    private static readonly Regex FromImportRegex = new(
        @"^\s*from\s+([\w.]+)\s+import\s+(.+)",
        RegexOptions.Compiled);

    /// <summary>Matches: class ClassName(bases):</summary>
    private static readonly Regex ClassRegex = new(
        @"^\s*class\s+(\w+)\s*(?:\(([^)]*)\))?\s*:",
        RegexOptions.Compiled);

    /// <summary>Matches: def function_name(params):</summary>
    private static readonly Regex FunctionRegex = new(
        @"^\s*(?:async\s+)?def\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Matches: @decorator</summary>
    private static readonly Regex DecoratorRegex = new(
        @"^\s*@(\w[\w.]*)",
        RegexOptions.Compiled);

    /// <summary>Matches top-level variable assignment: VAR_NAME = ...</summary>
    private static readonly Regex TopLevelVarRegex = new(
        @"^([A-Z_][A-Z0-9_]*)\s*[=:]",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Python" };

    public PythonParser(ILogger<PythonParser> logger)
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

        var files = FindPythonFiles(repoPath);
        _logger.LogDebug("PythonParser: found {Count} .py files", files.Count);

        var allSymbols = new List<SymbolInfo>();
        var allNodes = new List<GraphNode>();
        var allEdges = new List<GraphEdge>();

        // Build a set of all relative paths for import resolution
        var allRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(repoPath, f).Replace('\\', '/');
            allRelativePaths.Add(rel);
        }

        foreach (var file in files)
        {
            var fi = new System.IO.FileInfo(file);
            if (fi.Length > MaxParseFileSize) continue;

            var relativePath = Path.GetRelativePath(repoPath, file).Replace('\\', '/');
            var parsed = ParseFile(relativePath, file, allRelativePaths);

            allSymbols.AddRange(parsed.Symbols);
            allNodes.AddRange(parsed.Nodes);
            allEdges.AddRange(parsed.Edges);
        }

        _logger.LogInformation(
            "PythonParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
            allSymbols.Count, allNodes.Count, allEdges.Count);

        _lastRepoPath = repoPath;
        _lastResult = new CombinedResult(allSymbols, allNodes, allEdges);
        return Task.FromResult(_lastResult);
    }

    // ─── File-level parsing ────────────────────────────────────────────

    private static FileParseResult ParseFile(
        string relativePath, string absolutePath, HashSet<string> allRelativePaths)
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

        string? currentClass = null;
        string? currentClassNodeId = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;

            // ── decorators ──
            var decoratorMatch = DecoratorRegex.Match(line);
            if (decoratorMatch.Success)
            {
                // Track decorators as symbols for search but no graph node
                symbols.Add(new SymbolInfo
                {
                    Name = $"@{decoratorMatch.Groups[1].Value}",
                    Kind = SymbolKind.Function, // Closest existing kind
                    FilePath = relativePath,
                    Line = lineNum
                });
                continue;
            }

            // ── import X ──
            var importMatch = ImportRegex.Match(line);
            if (importMatch.Success)
            {
                var moduleName = importMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = moduleName,
                    Kind = SymbolKind.Import,
                    FilePath = relativePath,
                    Line = lineNum
                });
                AddImportEdge(edges, moduleId, moduleName, relativePath, allRelativePaths);
                continue;
            }

            // ── from X import Y ──
            var fromMatch = FromImportRegex.Match(line);
            if (fromMatch.Success)
            {
                var fromModule = fromMatch.Groups[1].Value;
                var importedNames = fromMatch.Groups[2].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = $"{fromModule}.{importedNames.Trim()}",
                    Kind = SymbolKind.Import,
                    FilePath = relativePath,
                    Line = lineNum
                });
                AddImportEdge(edges, moduleId, fromModule, relativePath, allRelativePaths);
                continue;
            }

            // ── class declaration ──
            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                var className = classMatch.Groups[1].Value;
                var indent = line.Length - line.TrimStart().Length;

                // Only top-level or reasonably nested classes
                if (indent <= 4)
                {
                    currentClass = className;
                    currentClassNodeId = $"class:{StripExtension(relativePath)}.{className}";

                    symbols.Add(new SymbolInfo
                    {
                        Name = className,
                        Kind = SymbolKind.Class,
                        FilePath = relativePath,
                        Line = lineNum
                    });

                    nodes.Add(new GraphNode
                    {
                        Id = currentClassNodeId,
                        Name = className,
                        Type = NodeType.Class,
                        FilePath = relativePath
                    });

                    // Module → Contains → Class
                    edges.Add(new GraphEdge
                    {
                        Source = moduleId,
                        Target = currentClassNodeId,
                        Relationship = EdgeRelationship.Contains
                    });

                    // Inheritance edges from base classes
                    if (classMatch.Groups[2].Success && !string.IsNullOrWhiteSpace(classMatch.Groups[2].Value))
                    {
                        foreach (var baseName in classMatch.Groups[2].Value.Split(','))
                        {
                            var trimmed = baseName.Trim();
                            // Filter out common non-class bases
                            if (!string.IsNullOrEmpty(trimmed) &&
                                trimmed != "object" &&
                                trimmed != "ABC" &&
                                !trimmed.StartsWith("metaclass="))
                            {
                                edges.Add(new GraphEdge
                                {
                                    Source = currentClassNodeId,
                                    Target = $"type:{trimmed}",
                                    Relationship = EdgeRelationship.Inherits
                                });
                            }
                        }
                    }
                }
                continue;
            }

            // ── function / method definition ──
            var funcMatch = FunctionRegex.Match(line);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                var indent = line.Length - line.TrimStart().Length;

                // Determine if this is a method (indented inside a class) or top-level function
                var isMethod = currentClass is not null && indent >= 4;
                var kind = isMethod ? SymbolKind.Method : SymbolKind.Function;

                symbols.Add(new SymbolInfo
                {
                    Name = funcName,
                    Kind = kind,
                    FilePath = relativePath,
                    Line = lineNum,
                    ParentSymbol = isMethod ? currentClass : null
                });

                // Only create graph nodes for top-level functions and public methods
                if (!isMethod)
                {
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
                }

                // Reset class context if we encounter top-level def
                if (indent == 0)
                {
                    currentClass = null;
                    currentClassNodeId = null;
                }
                continue;
            }

            // ── top-level constant/variable (ALL_CAPS) ──
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                var varMatch = TopLevelVarRegex.Match(line);
                if (varMatch.Success)
                {
                    symbols.Add(new SymbolInfo
                    {
                        Name = varMatch.Groups[1].Value,
                        Kind = SymbolKind.Variable,
                        FilePath = relativePath,
                        Line = lineNum
                    });
                }

                // Reset class context on any non-indented non-class line
                if (!classMatch.Success && !funcMatch.Success)
                {
                    currentClass = null;
                    currentClassNodeId = null;
                }
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    // ─── Import resolution ─────────────────────────────────────────────

    /// <summary>
    /// Resolves a Python import to a file in the repo and adds an edge.
    /// Handles relative dot imports and dotted modules.
    /// </summary>
    private static void AddImportEdge(
        List<GraphEdge> edges, string sourceModuleId,
        string importPath, string currentFilePath,
        HashSet<string> allRelativePaths)
    {
        var resolved = ResolveImport(importPath, currentFilePath, allRelativePaths);
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
    /// Resolves a Python module path (e.g. "mypackage.submod") to a file path.
    /// Returns null for external (third-party) packages.
    /// </summary>
    private static string? ResolveImport(
        string modulePath, string currentFilePath,
        HashSet<string> allRelativePaths)
    {
        var currentDir = Path.GetDirectoryName(currentFilePath)?.Replace('\\', '/') ?? "";

        // Handle relative imports (leading dots)
        if (modulePath.StartsWith('.'))
        {
            var dots = modulePath.TakeWhile(c => c == '.').Count();
            var rest = modulePath[dots..];
            var parts = currentDir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

            // Go up (dots - 1) levels
            for (var i = 1; i < dots && parts.Count > 0; i++)
                parts.RemoveAt(parts.Count - 1);

            var basePath = string.Join('/', parts);
            if (!string.IsNullOrEmpty(rest))
            {
                var restPath = rest.Replace('.', '/');
                basePath = string.IsNullOrEmpty(basePath) ? restPath : $"{basePath}/{restPath}";
            }

            return TryResolvePythonFile(basePath, allRelativePaths);
        }

        // Absolute import: convert dots to path separators
        var candidate = modulePath.Replace('.', '/');

        // Try relative to current directory first
        if (!string.IsNullOrEmpty(currentDir))
        {
            var rel = $"{currentDir}/{candidate}";
            var resolved = TryResolvePythonFile(rel, allRelativePaths);
            if (resolved is not null) return resolved;
        }

        // Try from repo root
        return TryResolvePythonFile(candidate, allRelativePaths);
    }

    /// <summary>
    /// Attempts to match a module path to a Python file or package __init__.py.
    /// </summary>
    private static string? TryResolvePythonFile(string basePath, HashSet<string> allPaths)
    {
        // Direct .py file
        var pyFile = $"{basePath}.py";
        if (allPaths.Contains(pyFile)) return pyFile;

        // Package __init__.py
        var initFile = $"{basePath}/__init__.py";
        if (allPaths.Contains(initFile)) return initFile;

        return null;
    }

    private static string StripExtension(string path)
    {
        var lastDot = path.LastIndexOf('.');
        var lastSlash = path.LastIndexOf('/');
        return lastDot > lastSlash ? path[..lastDot] : path;
    }

    // ─── File discovery ────────────────────────────────────────────────

    private static List<string> FindPythonFiles(string rootPath)
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
                if (PythonExtensions.Contains(ext))
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
