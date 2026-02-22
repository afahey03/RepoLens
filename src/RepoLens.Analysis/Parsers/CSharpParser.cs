using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses C# source files using regex and heuristic-based text analysis.
/// Extracts namespaces, types (class, interface, record, struct, enum),
/// methods, properties, and using directives.
/// Builds dependency graph nodes for namespaces/types and edges for
/// imports, inheritance, and containment.
/// </summary>
public class CSharpParser : ILanguageParser
{
    private readonly ILogger<CSharpParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode", "wwwroot"
    };

    /// <summary>
    /// Max file size to attempt parsing (1 MB). Larger files are likely generated.
    /// </summary>
    private const long MaxParseFileSize = 1 * 1024 * 1024;

    // ─── Regex patterns ────────────────────────────────────────────────

    /// <summary>
    /// Matches: using Namespace.Name; (excludes using static, aliases, declarations)
    /// </summary>
    private static readonly Regex UsingRegex = new(
        @"^\s*using\s+(?!static\s)(?!var\s)(?!\w+\s*=)([\w.]+)\s*;",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches: namespace X.Y.Z { ... } or namespace X.Y.Z; (file-scoped)
    /// </summary>
    private static readonly Regex NamespaceRegex = new(
        @"^\s*namespace\s+([\w.]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches type keyword + name inside a type declaration line.
    /// </summary>
    private static readonly Regex TypeDeclRegex = new(
        @"\b(class|interface|record|struct|enum)\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Captures base/implemented types after a colon in a type declaration.
    /// </summary>
    private static readonly Regex BaseTypesRegex = new(
        @"\b(?:class|interface|record|struct)\s+\w+(?:<[^>]+>)?(?:\s*\([^)]*\))?\s*:\s*([\w.,\s<>]+?)(?:\s*(?:where\b|\{|$))",
        RegexOptions.Compiled);

    private static readonly HashSet<string> AccessModifiers = new()
    {
        "public", "private", "protected", "internal"
    };

    private static readonly HashSet<string> Keywords = new()
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case",
        "break", "continue", "return", "throw", "try", "catch", "finally",
        "lock", "using", "new", "this", "base", "null", "true", "false",
        "var", "in", "out", "ref", "is", "as", "typeof", "sizeof", "nameof",
        "where", "select", "from", "value", "get", "set", "init", "when",
        "yield", "await", "async", "default", "checked", "unchecked", "fixed",
        "delegate", "event", "operator", "implicit", "explicit", "params",
        "string", "int", "bool", "void", "object", "double", "float",
        "long", "short", "byte", "char", "decimal", "uint", "ulong", "ushort"
    };

    public IReadOnlySet<string> SupportedLanguages { get; } = new HashSet<string> { "C#" };

    public CSharpParser(ILogger<CSharpParser> logger)
    {
        _logger = logger;
    }

    // ─── Cache to avoid double-parsing ─────────────────────────────────

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
        var knownNamespaces = new HashSet<string>();
        var createdNodeIds = new HashSet<string>();
        var tentativeImportEdges = new List<GraphEdge>();

        var files = FindCSharpFiles(repoPath);
        _logger.LogInformation("C# parser: found {Count} .cs files to parse", files.Count);

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            if (new System.IO.FileInfo(filePath).Length > MaxParseFileSize)
                continue;

            try
            {
                var relativePath = Path.GetRelativePath(repoPath, filePath).Replace('\\', '/');
                var fileResult = await ParseFileAsync(filePath, relativePath, ct);

                symbols.AddRange(fileResult.Symbols);

                foreach (var node in fileResult.Nodes)
                {
                    if (createdNodeIds.Add(node.Id))
                        nodes.Add(node);
                }

                foreach (var ns in fileResult.Namespaces)
                    knownNamespaces.Add(ns);

                tentativeImportEdges.AddRange(fileResult.ImportEdges);
                edges.AddRange(fileResult.OtherEdges);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("C# parser: failed to parse {File}: {Error}", filePath, ex.Message);
            }
        }

        // Create namespace graph nodes
        foreach (var ns in knownNamespaces)
        {
            var nsId = $"ns:{ns}";
            if (createdNodeIds.Add(nsId))
            {
                nodes.Add(new GraphNode
                {
                    Id = nsId,
                    Name = ns,
                    Type = NodeType.Namespace
                });
            }
        }

        // Only keep import edges targeting namespaces defined in this repo
        foreach (var edge in tentativeImportEdges)
        {
            var targetNs = edge.Target.StartsWith("ns:") ? edge.Target[3..] : edge.Target;
            if (knownNamespaces.Contains(targetNs))
                edges.Add(edge);
        }

        _logger.LogInformation(
            "C# parser: extracted {Symbols} symbols, {Nodes} graph nodes, {Edges} edges",
            symbols.Count, nodes.Count, edges.Count);

        return new CombinedResult(symbols, nodes, edges);
    }

    // ─── File-level parsing ────────────────────────────────────────────

    private async Task<FileParseResult> ParseFileAsync(
        string filePath, string relativePath, CancellationToken ct)
    {
        var symbols = new List<SymbolInfo>();
        var nodes = new List<GraphNode>();
        var importEdges = new List<GraphEdge>();
        var otherEdges = new List<GraphEdge>();
        var namespaces = new HashSet<string>();

        var lines = await File.ReadAllLinesAsync(filePath, ct);
        string? currentNamespace = null;
        string? currentType = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed.StartsWith("//") ||
                trimmed.StartsWith("/*") || trimmed.StartsWith("*") ||
                trimmed.StartsWith('#') || trimmed.StartsWith('['))
                continue;

            // ── Using directives ──────────────────────────────────
            var usingMatch = UsingRegex.Match(line);
            if (usingMatch.Success)
            {
                var ns = usingMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo
                {
                    Name = ns,
                    Kind = SymbolKind.Import,
                    FilePath = relativePath,
                    Line = lineNum
                });
                importEdges.Add(new GraphEdge
                {
                    Source = relativePath,
                    Target = $"ns:{ns}",
                    Relationship = EdgeRelationship.Imports
                });
                continue;
            }

            // ── Namespace declarations ────────────────────────────
            var nsMatch = NamespaceRegex.Match(line);
            if (nsMatch.Success)
            {
                currentNamespace = nsMatch.Groups[1].Value;
                namespaces.Add(currentNamespace);
                symbols.Add(new SymbolInfo
                {
                    Name = currentNamespace,
                    Kind = SymbolKind.Namespace,
                    FilePath = relativePath,
                    Line = lineNum
                });
                continue;
            }

            // ── Type declarations ─────────────────────────────────
            var typeMatch = TypeDeclRegex.Match(line);
            if (typeMatch.Success && LineHasModifier(trimmed))
            {
                var typeKeyword = typeMatch.Groups[1].Value;
                var typeName = typeMatch.Groups[2].Value;

                var symbolKind = typeKeyword == "interface"
                    ? SymbolKind.Interface : SymbolKind.Class;
                var nodeType = typeKeyword == "interface"
                    ? NodeType.Interface : NodeType.Class;

                var fullName = currentNamespace is not null
                    ? $"{currentNamespace}.{typeName}" : typeName;
                var nodeId = $"{nodeType.ToString().ToLowerInvariant()}:{fullName}";
                currentType = typeName;

                symbols.Add(new SymbolInfo
                {
                    Name = typeName,
                    Kind = symbolKind,
                    FilePath = relativePath,
                    Line = lineNum,
                    ParentSymbol = currentNamespace
                });

                nodes.Add(new GraphNode
                {
                    Id = nodeId,
                    Name = typeName,
                    Type = nodeType,
                    FilePath = relativePath,
                    Metadata = new Dictionary<string, string>
                    {
                        ["namespace"] = currentNamespace ?? "",
                        ["fullName"] = fullName,
                        ["kind"] = typeKeyword
                    }
                });

                // File → Contains → Type
                otherEdges.Add(new GraphEdge
                {
                    Source = relativePath,
                    Target = nodeId,
                    Relationship = EdgeRelationship.Contains
                });

                // Namespace → Contains → Type
                if (currentNamespace is not null)
                {
                    otherEdges.Add(new GraphEdge
                    {
                        Source = $"ns:{currentNamespace}",
                        Target = nodeId,
                        Relationship = EdgeRelationship.Contains
                    });
                }

                // Inheritance / interface implementation
                var baseMatch = BaseTypesRegex.Match(line);
                if (baseMatch.Success)
                {
                    foreach (var raw in baseMatch.Groups[1].Value.Split(','))
                    {
                        var baseType = raw.Trim().Split('<')[0].Trim();
                        if (string.IsNullOrEmpty(baseType)) continue;

                        var relationship = baseType.Length > 1
                            && baseType[0] == 'I'
                            && char.IsUpper(baseType[1])
                            ? EdgeRelationship.Implements
                            : EdgeRelationship.Inherits;

                        otherEdges.Add(new GraphEdge
                        {
                            Source = nodeId,
                            Target = $"type:{baseType}",
                            Relationship = relationship
                        });
                    }
                }

                continue;
            }

            // ── Methods & Properties (only inside a type) ─────────
            if (currentType is null || !LineHasModifier(trimmed))
                continue;

            // Property: line contains { get/set/init
            if (IsPropertyLine(trimmed))
            {
                var propName = ExtractNameBefore(trimmed, '{');
                if (propName is not null && !Keywords.Contains(propName))
                {
                    symbols.Add(new SymbolInfo
                    {
                        Name = propName,
                        Kind = SymbolKind.Property,
                        FilePath = relativePath,
                        Line = lineNum,
                        ParentSymbol = currentType
                    });
                }
                continue;
            }

            // Method: line has ( and is not a field assignment
            if (trimmed.Contains('(') && !trimmed.Contains(" = "))
            {
                var methodName = ExtractNameBefore(trimmed, '(');
                if (methodName is not null && !Keywords.Contains(methodName))
                {
                    symbols.Add(new SymbolInfo
                    {
                        Name = methodName,
                        Kind = SymbolKind.Method,
                        FilePath = relativePath,
                        Line = lineNum,
                        ParentSymbol = currentType
                    });
                }
            }
        }

        return new FileParseResult(symbols, nodes, importEdges, otherEdges, namespaces);
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static bool LineHasModifier(string trimmedLine)
    {
        var firstWord = FirstWord(trimmedLine);
        return AccessModifiers.Contains(firstWord)
            || firstWord is "static" or "abstract" or "sealed" or "partial"
                or "readonly" or "async" or "override" or "virtual"
                or "new" or "extern" or "unsafe";
    }

    private static string FirstWord(string line)
    {
        var end = line.IndexOfAny([' ', '\t', '(', '<', '{']);
        return end > 0 ? line[..end] : line;
    }

    private static bool IsPropertyLine(string trimmed) =>
        trimmed.Contains("{ get", StringComparison.Ordinal) ||
        trimmed.Contains("{ set", StringComparison.Ordinal) ||
        trimmed.Contains("{ init", StringComparison.Ordinal) ||
        trimmed.Contains("{get", StringComparison.Ordinal) ||
        trimmed.Contains("{set", StringComparison.Ordinal);

    /// <summary>
    /// Extracts the identifier immediately before a delimiter character.
    /// For methods the delimiter is '('; for properties it is '{'.
    /// </summary>
    private static string? ExtractNameBefore(string trimmedLine, char delimiter)
    {
        var idx = trimmedLine.IndexOf(delimiter);
        if (idx <= 0) return null;

        var before = trimmedLine.AsSpan(0, idx).TrimEnd();
        var end = before.Length;

        // Skip generic type parameters like <T>
        if (end > 0 && before[end - 1] == '>')
        {
            var depth = 1;
            end -= 2;
            while (end >= 0 && depth > 0)
            {
                if (before[end] == '>') depth++;
                else if (before[end] == '<') depth--;
                end--;
            }
            end++;
            before = before[..end].TrimEnd();
            end = before.Length;
        }

        var lastSep = before.LastIndexOfAny([' ', '\t']);
        if (lastSep < 0) return null;

        var name = before[(lastSep + 1)..].Trim().ToString();
        return name.Length > 0 && char.IsLetter(name[0]) ? name : null;
    }

    // ─── File discovery ────────────────────────────────────────────────

    private static List<string> FindCSharpFiles(string rootPath)
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

            foreach (var file in Directory.GetFiles(dir, "*.cs"))
                result.Add(file);
        }
        catch (UnauthorizedAccessException) { }
    }

    // ─── Result types ──────────────────────────────────────────────────

    private record CombinedResult(
        List<SymbolInfo> Symbols, List<GraphNode> Nodes, List<GraphEdge> Edges);

    private record FileParseResult(
        List<SymbolInfo> Symbols,
        List<GraphNode> Nodes,
        List<GraphEdge> ImportEdges,
        List<GraphEdge> OtherEdges,
        HashSet<string> Namespaces);
}
