using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Java source files using regex-based text analysis.
/// Extracts packages, imports, classes, interfaces, enums, annotations,
/// methods, and fields. Builds dependency graph nodes and edges for
/// containment, imports, inheritance, and interface implementation.
/// </summary>
public class JavaParser : ILanguageParser
{
    private readonly ILogger<JavaParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "target", "build", ".gradle", ".mvn", "out", ".settings"
    };

    /// <summary>Max file size to attempt parsing (1 MB).</summary>
    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> JavaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".java"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    /// <summary>Matches: package com.example.project;</summary>
    private static readonly Regex PackageRegex = new(
        @"^\s*package\s+([\w.]+)\s*;",
        RegexOptions.Compiled);

    /// <summary>Matches: import com.example.ClassName; or import static ...</summary>
    private static readonly Regex ImportRegex = new(
        @"^\s*import\s+(?:static\s+)?([\w.*]+)\s*;",
        RegexOptions.Compiled);

    /// <summary>Matches: class ClassName extends Base implements I1, I2</summary>
    private static readonly Regex ClassRegex = new(
        @"\b(?:public|protected|private|abstract|final|static)?\s*class\s+(\w+)" +
        @"(?:\s*<[^>]+>)?" +  // generics
        @"(?:\s+extends\s+(\w+(?:\.\w+)*))?" +
        @"(?:\s+implements\s+([\w.,\s<>]+))?\s*\{?",
        RegexOptions.Compiled);

    /// <summary>Matches: interface InterfaceName extends I1, I2</summary>
    private static readonly Regex InterfaceRegex = new(
        @"\b(?:public|protected|private)?\s*interface\s+(\w+)" +
        @"(?:\s*<[^>]+>)?" +
        @"(?:\s+extends\s+([\w.,\s<>]+))?\s*\{?",
        RegexOptions.Compiled);

    /// <summary>Matches: enum EnumName { ... }</summary>
    private static readonly Regex EnumRegex = new(
        @"\b(?:public|protected|private)?\s*enum\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>Matches: @AnnotationName</summary>
    private static readonly Regex AnnotationRegex = new(
        @"^\s*@(\w+)",
        RegexOptions.Compiled);

    /// <summary>Matches method declarations with access modifiers and return types.</summary>
    private static readonly Regex MethodRegex = new(
        @"^\s+(?:(?:public|protected|private|static|final|abstract|synchronized|native)\s+)*" +
        @"(?:<[\w,\s?]+>\s+)?" +  // generic return type
        @"([\w<>\[\].,\s?]+)\s+(\w+)\s*\([^)]*\)\s*(?:throws\s+[\w.,\s]+)?\s*[{;]",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Java" };

    public JavaParser(ILogger<JavaParser> logger)
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

        var files = FindJavaFiles(repoPath);
        _logger.LogDebug("JavaParser: found {Count} .java files", files.Count);

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
            "JavaParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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

        // Module node representing this file
        var moduleId = $"module:{StripExtension(relativePath)}";
        nodes.Add(new GraphNode
        {
            Id = moduleId,
            Name = Path.GetFileNameWithoutExtension(relativePath),
            Type = NodeType.Module,
            FilePath = relativePath
        });

        string? currentPackage = null;
        string? currentClass = null;
        string? currentClassNodeId = null;
        var braceDepth = 0;
        var classStartBraceDepth = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            // Skip comments
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*"))
                continue;

            // Track brace depth for class scope
            foreach (var ch in line)
            {
                if (ch == '{') braceDepth++;
                else if (ch == '}')
                {
                    braceDepth--;
                    // If we drop back to the class start depth, we've exited the class
                    if (currentClass is not null && braceDepth <= classStartBraceDepth)
                    {
                        currentClass = null;
                        currentClassNodeId = null;
                        classStartBraceDepth = -1;
                    }
                }
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

                var nsNodeId = $"namespace:{currentPackage}";
                nodes.Add(new GraphNode
                {
                    Id = nsNodeId,
                    Name = currentPackage,
                    Type = NodeType.Namespace,
                    FilePath = relativePath
                });

                // Namespace → Contains → Module
                edges.Add(new GraphEdge
                {
                    Source = nsNodeId,
                    Target = moduleId,
                    Relationship = EdgeRelationship.Contains
                });
                continue;
            }

            // ── import declaration ──
            var importMatch = ImportRegex.Match(line);
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
                AddImportEdge(edges, moduleId, importPath, allRelativePaths);
                continue;
            }

            // ── annotation ──
            var annotationMatch = AnnotationRegex.Match(line);
            if (annotationMatch.Success &&
                !trimmed.Contains("class ") && !trimmed.Contains("interface "))
            {
                symbols.Add(new SymbolInfo
                {
                    Name = $"@{annotationMatch.Groups[1].Value}",
                    Kind = SymbolKind.Function, // Closest existing kind
                    FilePath = relativePath,
                    Line = lineNum
                });
                // Don't continue — the line might also have a class/method on it
            }

            // ── class declaration ──
            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success && !IsInsideComment(line, classMatch.Index))
            {
                var className = classMatch.Groups[1].Value;
                currentClass = className;
                classStartBraceDepth = braceDepth - (line.Contains('{') ? 1 : 0);
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

                // extends
                if (classMatch.Groups[2].Success && classMatch.Groups[2].Value.Length > 0)
                {
                    var baseName = classMatch.Groups[2].Value.Trim();
                    // Strip generic params
                    var genericIdx = baseName.IndexOf('<');
                    if (genericIdx > 0) baseName = baseName[..genericIdx];

                    edges.Add(new GraphEdge
                    {
                        Source = currentClassNodeId,
                        Target = $"type:{baseName}",
                        Relationship = EdgeRelationship.Inherits
                    });
                }

                // implements
                if (classMatch.Groups[3].Success && classMatch.Groups[3].Value.Length > 0)
                {
                    foreach (var iface in classMatch.Groups[3].Value.Split(','))
                    {
                        var name = iface.Trim();
                        var genericIdx = name.IndexOf('<');
                        if (genericIdx > 0) name = name[..genericIdx];

                        if (!string.IsNullOrEmpty(name))
                        {
                            edges.Add(new GraphEdge
                            {
                                Source = currentClassNodeId,
                                Target = $"type:{name}",
                                Relationship = EdgeRelationship.Implements
                            });
                        }
                    }
                }
                continue;
            }

            // ── interface declaration ──
            var ifaceMatch = InterfaceRegex.Match(line);
            if (ifaceMatch.Success && !IsInsideComment(line, ifaceMatch.Index))
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

                // Module → Contains → Interface
                edges.Add(new GraphEdge
                {
                    Source = moduleId,
                    Target = ifaceNodeId,
                    Relationship = EdgeRelationship.Contains
                });

                // Interface extends other interfaces
                if (ifaceMatch.Groups[2].Success && ifaceMatch.Groups[2].Value.Length > 0)
                {
                    foreach (var baseIface in ifaceMatch.Groups[2].Value.Split(','))
                    {
                        var name = baseIface.Trim();
                        var genericIdx = name.IndexOf('<');
                        if (genericIdx > 0) name = name[..genericIdx];

                        if (!string.IsNullOrEmpty(name))
                        {
                            edges.Add(new GraphEdge
                            {
                                Source = ifaceNodeId,
                                Target = $"type:{name}",
                                Relationship = EdgeRelationship.Inherits
                            });
                        }
                    }
                }
                continue;
            }

            // ── enum declaration ──
            var enumMatch = EnumRegex.Match(line);
            if (enumMatch.Success && !IsInsideComment(line, enumMatch.Index))
            {
                var enumName = enumMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = enumName,
                    Kind = SymbolKind.Class, // Closest existing kind
                    FilePath = relativePath,
                    Line = lineNum
                });

                var enumNodeId = $"class:{StripExtension(relativePath)}.{enumName}";
                nodes.Add(new GraphNode
                {
                    Id = enumNodeId,
                    Name = enumName,
                    Type = NodeType.Class,
                    FilePath = relativePath
                });

                edges.Add(new GraphEdge
                {
                    Source = moduleId,
                    Target = enumNodeId,
                    Relationship = EdgeRelationship.Contains
                });
                continue;
            }

            // ── method declaration (inside a class) ──
            if (currentClass is not null)
            {
                var methodMatch = MethodRegex.Match(line);
                if (methodMatch.Success)
                {
                    var methodName = methodMatch.Groups[2].Value;

                    // Skip constructor (same name as class) — still add as symbol
                    symbols.Add(new SymbolInfo
                    {
                        Name = methodName,
                        Kind = SymbolKind.Method,
                        FilePath = relativePath,
                        Line = lineNum,
                        ParentSymbol = currentClass
                    });
                }
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    // ─── Import resolution ─────────────────────────────────────────────

    /// <summary>
    /// Resolves a Java import to a file in the repo and adds an edge.
    /// External (library) imports that don't map to a local file are skipped.
    /// </summary>
    private static void AddImportEdge(
        List<GraphEdge> edges, string sourceModuleId,
        string importPath, HashSet<string> allRelativePaths)
    {
        var resolved = ResolveImport(importPath, allRelativePaths);
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
    /// Resolves a Java import path (e.g. "com.example.MyClass") to a file path.
    /// Checks for class file or wildcard package imports.
    /// Returns null for external library imports.
    /// </summary>
    private static string? ResolveImport(string importPath, HashSet<string> allRelativePaths)
    {
        // Strip wildcard imports (com.example.*)
        var path = importPath.EndsWith(".*")
            ? importPath[..^2]
            : importPath;

        // Convert package.Class to path/Class.java
        var filePath = path.Replace('.', '/') + ".java";
        if (allRelativePaths.Contains(filePath))
            return filePath;

        // Try as directory (for wildcard imports, look for any .java file in that package)
        // Just try the package name as a path prefix
        var dirPrefix = path.Replace('.', '/') + "/";
        foreach (var rp in allRelativePaths)
        {
            if (rp.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                return rp; // Return first match in that package
        }

        return null;
    }

    /// <summary>
    /// Simple check: is the given position inside a string or comment?
    /// </summary>
    private static bool IsInsideComment(string line, int position)
    {
        var commentIdx = line.IndexOf("//", StringComparison.Ordinal);
        return commentIdx >= 0 && commentIdx < position;
    }

    private static string StripExtension(string path)
    {
        var lastDot = path.LastIndexOf('.');
        var lastSlash = path.LastIndexOf('/');
        return lastDot > lastSlash ? path[..lastDot] : path;
    }

    // ─── File discovery ────────────────────────────────────────────────

    private static List<string> FindJavaFiles(string rootPath)
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
                if (JavaExtensions.Contains(ext))
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
