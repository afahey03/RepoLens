using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses C++ source files using regex-based text analysis.
/// Extracts #include directives, namespaces, classes, structs, enums,
/// templates, function/method definitions, and inheritance.
/// Builds dependency graph nodes and edges.
/// </summary>
public class CppParser : ILanguageParser
{
    private readonly ILogger<CppParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode",
        "build", "cmake-build-debug", "cmake-build-release", ".cache",
        "third_party", "external", "deps", "out"
    };

    private const long MaxParseFileSize = 1 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpp", ".cxx", ".cc", ".hpp", ".hxx", ".hh", ".h++"
    };

    // ─── Regex patterns ────────────────────────────────────────────────

    private static readonly Regex IncludeRegex = new(
        @"^\s*#\s*include\s+[""<]([^"">]+)[>""]",
        RegexOptions.Compiled);

    private static readonly Regex NamespaceRegex = new(
        @"^\s*namespace\s+([\w:]+)",
        RegexOptions.Compiled);

    private static readonly Regex ClassRegex = new(
        @"^\s*(?:template\s*<[^>]*>\s*)?(?:class|struct)\s+(?:\w+\s+)*(\w+)(?:\s*:\s*(?:public|protected|private)\s+([\w:,\s]+))?\s*\{?",
        RegexOptions.Compiled);

    private static readonly Regex EnumRegex = new(
        @"^\s*enum\s+(?:class\s+)?(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex FunctionRegex = new(
        @"^(?!#)(?:[\w*&:<>,\s]+)\s+(\w+)\s*\([^;]*\)\s*(?:const\s*)?(?:override\s*)?(?:noexcept\s*)?(?:final\s*)?\{",
        RegexOptions.Compiled);

    private static readonly Regex MethodRegex = new(
        @"^(?:[\w*&:<>,\s]+)\s+(\w+)::(\w+)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex UsingRegex = new(
        @"^\s*using\s+(?:namespace\s+)?([\w:]+)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex DefineRegex = new(
        @"^\s*#\s*define\s+(\w+)",
        RegexOptions.Compiled);

    // ─── ILanguageParser implementation ────────────────────────────────

    public IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C++" };

    public CppParser(ILogger<CppParser> logger) => _logger = logger;

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
        _logger.LogDebug("CppParser: found {Count} C++ files", files.Count);

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

        _logger.LogInformation("CppParser: extracted {Symbols} symbols, {Nodes} nodes, {Edges} edges",
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
            Name = Path.GetFileName(relativePath),
            Type = NodeType.Module,
            FilePath = relativePath
        });

        string? currentNamespace = null;
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

            // #include
            var includeMatch = IncludeRegex.Match(line);
            if (includeMatch.Success)
            {
                var header = includeMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = header, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                ResolveInclude(edges, moduleId, header, relativePath, allRelativePaths);
                continue;
            }

            // #define
            var defineMatch = DefineRegex.Match(line);
            if (defineMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = defineMatch.Groups[1].Value, Kind = SymbolKind.Variable, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // namespace
            var nsMatch = NamespaceRegex.Match(line);
            if (nsMatch.Success)
            {
                currentNamespace = nsMatch.Groups[1].Value;
                symbols.Add(new SymbolInfo { Name = currentNamespace, Kind = SymbolKind.Namespace, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // using
            var usingMatch = UsingRegex.Match(line);
            if (usingMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = usingMatch.Groups[1].Value, Kind = SymbolKind.Import, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // class / struct
            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success && !line.TrimStart().StartsWith('#'))
            {
                var className = classMatch.Groups[1].Value;
                if (IsCppKeyword(className)) continue;

                var prefix = currentNamespace is not null ? $"{currentNamespace}." : "";
                var classNodeId = $"class:{StripExtension(relativePath)}.{prefix}{className}";

                symbols.Add(new SymbolInfo { Name = className, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                nodes.Add(new GraphNode { Id = classNodeId, Name = className, Type = NodeType.Class, FilePath = relativePath });
                edges.Add(new GraphEdge { Source = moduleId, Target = classNodeId, Relationship = EdgeRelationship.Contains });

                // Inheritance
                if (classMatch.Groups[2].Success)
                {
                    var bases = classMatch.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var b in bases)
                    {
                        var baseName = b.Replace("public ", "").Replace("protected ", "").Replace("private ", "").Trim();
                        if (!string.IsNullOrEmpty(baseName))
                        {
                            edges.Add(new GraphEdge { Source = classNodeId, Target = $"class:{baseName}", Relationship = EdgeRelationship.Inherits });
                        }
                    }
                }
                continue;
            }

            // enum
            var enumMatch = EnumRegex.Match(line);
            if (enumMatch.Success)
            {
                symbols.Add(new SymbolInfo { Name = enumMatch.Groups[1].Value, Kind = SymbolKind.Class, FilePath = relativePath, Line = lineNum });
                continue;
            }

            // Class::Method
            var methodMatch = MethodRegex.Match(line);
            if (methodMatch.Success)
            {
                var ownerClass = methodMatch.Groups[1].Value;
                var methodName = methodMatch.Groups[2].Value;
                if (!IsCppKeyword(methodName))
                {
                    symbols.Add(new SymbolInfo { Name = methodName, Kind = SymbolKind.Method, FilePath = relativePath, Line = lineNum, ParentSymbol = ownerClass });
                }
                continue;
            }

            // top-level function
            var funcMatch = FunctionRegex.Match(line);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                if (!IsCppKeyword(funcName) && !funcName.StartsWith('~'))
                {
                    var funcNodeId = $"func:{StripExtension(relativePath)}.{funcName}";
                    symbols.Add(new SymbolInfo { Name = funcName, Kind = SymbolKind.Function, FilePath = relativePath, Line = lineNum });
                    nodes.Add(new GraphNode { Id = funcNodeId, Name = funcName, Type = NodeType.Function, FilePath = relativePath });
                    edges.Add(new GraphEdge { Source = moduleId, Target = funcNodeId, Relationship = EdgeRelationship.Contains });
                }
            }
        }

        return new FileParseResult(symbols, nodes, edges);
    }

    private static bool IsCppKeyword(string name)
    {
        return name switch
        {
            "if" or "else" or "for" or "while" or "switch" or "case" or "return" or
            "break" or "continue" or "goto" or "do" or "sizeof" or "typedef" or
            "struct" or "union" or "enum" or "static" or "extern" or "inline" or
            "const" or "volatile" or "register" or "auto" or "void" or "define" or
            "class" or "namespace" or "template" or "virtual" or "override" or
            "public" or "private" or "protected" or "friend" or "new" or "delete" or
            "try" or "catch" or "throw" or "using" or "operator" or "nullptr" => true,
            _ => false
        };
    }

    private static void ResolveInclude(
        List<GraphEdge> edges, string sourceModuleId, string header,
        string currentFile, HashSet<string> allRelativePaths)
    {
        var fileName = Path.GetFileName(header);
        foreach (var rp in allRelativePaths)
        {
            if (rp.EndsWith(header, StringComparison.OrdinalIgnoreCase) ||
                rp.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                if (rp.Equals(currentFile, StringComparison.OrdinalIgnoreCase)) continue;
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
