using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses Dart source files using regex-based text analysis.
/// Extracts imports, library/part directives, classes, mixins,
/// extensions, enums, functions, and typedefs.
/// </summary>
public class DartParser : ILanguageParser
{
    private readonly ILogger<DartParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        ".dart_tool", "build", ".pub-cache"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dart"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex ImportRegex = new(
        @"^\s*import\s+['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex ExportRegex = new(
        @"^\s*export\s+['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex PartRegex = new(
        @"^\s*part\s+['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex PartOfRegex = new(
        @"^\s*part\s+of\s+['""]?([^'"";\s]+)",
        RegexOptions.Compiled);

    private static readonly Regex LibraryRegex = new(
        @"^\s*library\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex ClassRegex = new(
        @"^\s*(?:abstract\s+)?class\s+(\w+)(?:<[^>]*>)?(?:\s+extends\s+(\w+))?(?:\s+(?:with\s+([\w,\s]+))\s*)?(?:\s+implements\s+([\w,\s]+))?\s*\{?",
        RegexOptions.Compiled);

    private static readonly Regex MixinRegex = new(
        @"^\s*mixin\s+(\w+)(?:\s+on\s+([\w,\s]+))?\s*\{?",
        RegexOptions.Compiled);

    private static readonly Regex EnumRegex = new(
        @"^\s*enum\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex ExtensionRegex = new(
        @"^\s*extension\s+(\w+)\s+on\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex FunctionRegex = new(
        @"^\s*(?:static\s+)?(?:Future|Stream|void|int|double|String|bool|dynamic|var|\w+)(?:<[^>]*>)?\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex TopLevelFuncRegex = new(
        @"^(?:Future|Stream|void|int|double|String|bool|dynamic|var|\w+)(?:<[^>]*>)?\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex TypedefRegex = new(
        @"^\s*typedef\s+(\w+)",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dart" };

    public DartParser(ILogger<DartParser> logger) => _logger = logger;

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
        _logger.LogDebug("DartParser: found {Count} Dart files", files.Count);

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

        _logger.LogInformation("DartParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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

        string? currentType = null;
        var braceDepth = 0;
        var typeBraceDepth = -1;
        var inMultilineComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;
            var trimmed = line.TrimStart();

            // Block comment handling
            if (inMultilineComment)
            {
                if (trimmed.Contains("*/")) inMultilineComment = false;
                continue;
            }
            if (trimmed.StartsWith("/*"))
            {
                if (!trimmed.Contains("*/")) inMultilineComment = true;
                continue;
            }
            if (trimmed.StartsWith("//")) continue;

            // Track braces for scope
            foreach (var ch in line)
            {
                if (ch == '{') braceDepth++;
                else if (ch == '}') braceDepth--;
            }

            if (currentType is not null && braceDepth <= typeBraceDepth)
            {
                currentType = null;
                typeBraceDepth = -1;
            }

            // import
            var importMatch = ImportRegex.Match(line);
            if (importMatch.Success)
            {
                var importPath = importMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = importPath, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveImport(edges, moduleId, importPath, relativePath, allRelativePaths);
                continue;
            }

            // export
            var exportMatch = ExportRegex.Match(line);
            if (exportMatch.Success)
            {
                var exportPath = exportMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = exportPath, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // part
            var partMatch = PartRegex.Match(line);
            if (partMatch.Success)
            {
                var partPath = partMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = partPath, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // part of
            if (PartOfRegex.Match(line).Success) continue;

            // library
            var libMatch = LibraryRegex.Match(line);
            if (libMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = libMatch.Groups[1].Value, Kind = SymbolKind.Namespace, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // class
            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups[1].Value;
                var nodeId = $"class:{StripExtension(relativePath)}.{name}";
                currentType = name;
                typeBraceDepth = braceDepth - (line.Contains('{') ? 1 : 0);

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });

                // extends
                if (classMatch.Groups[2].Success)
                {
                    edges.Add(new GraphEdge { Source = nodeId, Target = $"class:{classMatch.Groups[2].Value}", Relationship = EdgeRelationship.Inherits });
                }

                // with (mixins)
                if (classMatch.Groups[3].Success)
                {
                    foreach (var mixin in classMatch.Groups[3].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
                    {
                        edges.Add(new GraphEdge { Source = nodeId, Target = $"interface:{mixin}", Relationship = EdgeRelationship.Implements });
                    }
                }

                // implements
                if (classMatch.Groups[4].Success)
                {
                    foreach (var iface in classMatch.Groups[4].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
                    {
                        edges.Add(new GraphEdge { Source = nodeId, Target = $"interface:{iface}", Relationship = EdgeRelationship.Implements });
                    }
                }
                continue;
            }

            // mixin
            var mixinMatch = MixinRegex.Match(line);
            if (mixinMatch.Success)
            {
                var name = mixinMatch.Groups[1].Value;
                var nodeId = $"interface:{name}";
                currentType = name;
                typeBraceDepth = braceDepth - (line.Contains('{') ? 1 : 0);

                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Interface, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = nodeId, Name = name, Type = NodeType.Interface, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = nodeId, Relationship = EdgeRelationship.Contains });
                continue;
            }

            // enum
            var enumMatch = EnumRegex.Match(line);
            if (enumMatch.Success)
            {
                var name = enumMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // extension
            var extMatch = ExtensionRegex.Match(line);
            if (extMatch.Success)
            {
                var name = extMatch.Groups[1].Value;
                currentType = name;
                typeBraceDepth = braceDepth - (line.Contains('{') ? 1 : 0);
                symbols.Add(new SymbolInfo { Name = name, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // typedef
            var typedefMatch = TypedefRegex.Match(line);
            if (typedefMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = typedefMatch.Groups[1].Value, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // functions / methods
            var funcMatch = currentType is null ? TopLevelFuncRegex.Match(line) : FunctionRegex.Match(line);
            if (funcMatch.Success)
            {
                var methodName = funcMatch.Groups[1].Value;
                if (IsKeyword(methodName)) continue;

                if (currentType is not null)
                {
                    symbols.Add(new SymbolInfo { Name = methodName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = currentType });
                }
                else
                {
                    var funcNodeId = $"func:{StripExtension(relativePath)}.{methodName}";
                    symbols.Add(new SymbolInfo { Name = methodName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                    nodes.Add(new GraphNode { Id = funcNodeId, Name = methodName, Type = NodeType.Function, FilePath = relativePath });
                    edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                }
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static bool IsKeyword(string name) =>
        name is "if" or "else" or "for" or "while" or "do" or "switch" or "case"
            or "return" or "break" or "continue" or "try" or "catch" or "finally"
            or "throw" or "new" or "var" or "final" or "const" or "class" or "enum";

    private static void ResolveImport(
        List<GraphEdge> edges, string sourceModuleId, string importPath,
        string currentFile, HashSet<string> allRelativePaths)
    {
        // package: imports are external, skip them
        if (importPath.StartsWith("package:") || importPath.StartsWith("dart:")) return;

        // relative import
        var dir = Path.GetDirectoryName(currentFile)?.Replace('\\', '/') ?? "";
        var candidate = string.IsNullOrEmpty(dir) ? importPath : $"{dir}/{importPath}";
        candidate = NormalizePath(candidate);

        foreach (var rp in allRelativePaths)
        {
            if (rp.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(new GraphEdge { Source = sourceModuleId, Target = $"module:{StripExtension(rp)}", Relationship = EdgeRelationship.Imports });
                return;
            }
        }
    }

    private static string NormalizePath(string path)
    {
        var parts = path.Split('/').ToList();
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".." && result.Count > 0) result.RemoveAt(result.Count - 1);
            else if (part != ".") result.Add(part);
        }
        return string.Join('/', result);
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
