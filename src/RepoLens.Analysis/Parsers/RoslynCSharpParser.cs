using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Models;

using SymbolInfo = RepoLens.Shared.Models.SymbolInfo;
using SymbolKind = RepoLens.Shared.Models.SymbolKind;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses C# source files using the Roslyn compiler API for AST-level accuracy.
/// Replaces the regex-based <see cref="CSharpParser"/> with full syntax-tree walking.
/// Extracts namespaces, types (class, interface, record, struct, enum),
/// methods, properties, constructors, and using directives.
/// Builds dependency graph nodes and edges for imports, inheritance,
/// implementation, and containment.
/// </summary>
public sealed class RoslynCSharpParser : ILanguageParser
{
    private readonly ILogger<RoslynCSharpParser> _logger;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea",
        "packages", "TestResults", ".nuget", ".github", ".vscode", "wwwroot"
    };

    /// <summary>
    /// Max file size to attempt parsing (1 MB). Larger files are likely generated.
    /// </summary>
    private const long MaxParseFileSize = 1 * 1024 * 1024;

    public IReadOnlySet<string> SupportedLanguages { get; } = new HashSet<string> { "C#" };

    public RoslynCSharpParser(ILogger<RoslynCSharpParser> logger)
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
        _logger.LogInformation("Roslyn C# parser: found {Count} .cs files to parse", files.Count);

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            if (new System.IO.FileInfo(filePath).Length > MaxParseFileSize)
                continue;

            try
            {
                var relativePath = Path.GetRelativePath(repoPath, filePath).Replace('\\', '/');
                var sourceText = await File.ReadAllTextAsync(filePath, ct);
                var tree = CSharpSyntaxTree.ParseText(
                    sourceText,
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                    path: filePath,
                    cancellationToken: ct);

                var root = await tree.GetRootAsync(ct);
                var walker = new SymbolWalker(relativePath);
                walker.Visit(root);

                symbols.AddRange(walker.Symbols);

                foreach (var node in walker.Nodes)
                {
                    if (createdNodeIds.Add(node.Id))
                        nodes.Add(node);
                }

                foreach (var ns in walker.Namespaces)
                    knownNamespaces.Add(ns);

                tentativeImportEdges.AddRange(walker.ImportEdges);
                edges.AddRange(walker.OtherEdges);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Roslyn C# parser: failed to parse {File}: {Error}", filePath, ex.Message);
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
            "Roslyn C# parser: extracted {Symbols} symbols, {Nodes} graph nodes, {Edges} edges",
            symbols.Count, nodes.Count, edges.Count);

        return new CombinedResult(symbols, nodes, edges);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Roslyn Syntax Walker — walks the full syntax tree for one file
    // ═══════════════════════════════════════════════════════════════════

    private sealed class SymbolWalker : CSharpSyntaxWalker
    {
        private readonly string _relativePath;
        private string? _currentNamespace;
        private string? _currentType;

        public List<SymbolInfo> Symbols { get; } = [];
        public List<GraphNode> Nodes { get; } = [];
        public List<GraphEdge> ImportEdges { get; } = [];
        public List<GraphEdge> OtherEdges { get; } = [];
        public HashSet<string> Namespaces { get; } = [];

        public SymbolWalker(string relativePath) : base(SyntaxWalkerDepth.Node)
        {
            _relativePath = relativePath;
        }

        // ── Using directives ──────────────────────────────────────

        public override void VisitUsingDirective(UsingDirectiveSyntax node)
        {
            // Skip using aliases (e.g. using Foo = Bar;) and static usings
            if (node.Alias is not null || node.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            {
                base.VisitUsingDirective(node);
                return;
            }

            var nameText = node.Name?.ToString();
            if (string.IsNullOrWhiteSpace(nameText))
            {
                base.VisitUsingDirective(node);
                return;
            }

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            Symbols.Add(new SymbolInfo
            {
                Name = nameText,
                Kind = SymbolKind.Import,
                FilePath = _relativePath,
                Line = line
            });

            ImportEdges.Add(new GraphEdge
            {
                Source = _relativePath,
                Target = $"ns:{nameText}",
                Relationship = EdgeRelationship.Imports
            });

            base.VisitUsingDirective(node);
        }

        // ── Namespace declarations ────────────────────────────────

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            HandleNamespace(node.Name.ToString(), node);
            base.VisitNamespaceDeclaration(node);
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            HandleNamespace(node.Name.ToString(), node);
            base.VisitFileScopedNamespaceDeclaration(node);
        }

        private void HandleNamespace(string name, SyntaxNode node)
        {
            _currentNamespace = name;
            Namespaces.Add(name);

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            Symbols.Add(new SymbolInfo
            {
                Name = name,
                Kind = SymbolKind.Namespace,
                FilePath = _relativePath,
                Line = line
            });
        }

        // ── Type declarations (class, interface, record, struct, enum) ──

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            HandleTypeDeclaration(node, node.Identifier.Text, "class", SymbolKind.Class, NodeType.Class, node.BaseList);
            base.VisitClassDeclaration(node);
            _currentType = null; // restore after leaving scope
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            HandleTypeDeclaration(node, node.Identifier.Text, "interface", SymbolKind.Interface, NodeType.Interface, node.BaseList);
            base.VisitInterfaceDeclaration(node);
            _currentType = null;
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            HandleTypeDeclaration(node, node.Identifier.Text, "record", SymbolKind.Class, NodeType.Class, node.BaseList);
            base.VisitRecordDeclaration(node);
            _currentType = null;
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            HandleTypeDeclaration(node, node.Identifier.Text, "struct", SymbolKind.Class, NodeType.Class, node.BaseList);
            base.VisitStructDeclaration(node);
            _currentType = null;
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            var name = node.Identifier.Text;
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var fullName = _currentNamespace is not null ? $"{_currentNamespace}.{name}" : name;
            var nodeId = $"class:{fullName}";

            _currentType = name;

            Symbols.Add(new SymbolInfo
            {
                Name = name,
                Kind = SymbolKind.Class,
                FilePath = _relativePath,
                Line = line,
                ParentSymbol = _currentNamespace
            });

            Nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = name,
                Type = NodeType.Class,
                FilePath = _relativePath,
                Metadata = new Dictionary<string, string>
                {
                    ["namespace"] = _currentNamespace ?? "",
                    ["fullName"] = fullName,
                    ["kind"] = "enum"
                }
            });

            AddContainmentEdges(nodeId);

            base.VisitEnumDeclaration(node);
            _currentType = null;
        }

        private void HandleTypeDeclaration(
            SyntaxNode node, string name, string keyword,
            SymbolKind symbolKind, NodeType nodeType, BaseListSyntax? baseList)
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var fullName = _currentNamespace is not null ? $"{_currentNamespace}.{name}" : name;
            var nodeId = $"{nodeType.ToString().ToLowerInvariant()}:{fullName}";

            _currentType = name;

            Symbols.Add(new SymbolInfo
            {
                Name = name,
                Kind = symbolKind,
                FilePath = _relativePath,
                Line = line,
                ParentSymbol = _currentNamespace
            });

            Nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = name,
                Type = nodeType,
                FilePath = _relativePath,
                Metadata = new Dictionary<string, string>
                {
                    ["namespace"] = _currentNamespace ?? "",
                    ["fullName"] = fullName,
                    ["kind"] = keyword
                }
            });

            AddContainmentEdges(nodeId);

            // Inheritance / interface implementation
            if (baseList is not null)
            {
                foreach (var baseTypeSyntax in baseList.Types)
                {
                    var baseTypeName = GetBaseTypeName(baseTypeSyntax.Type);
                    if (string.IsNullOrEmpty(baseTypeName)) continue;

                    // Convention: starts with I + uppercase => Implements, else Inherits
                    var relationship = baseTypeName.Length > 1
                        && baseTypeName[0] == 'I'
                        && char.IsUpper(baseTypeName[1])
                        ? EdgeRelationship.Implements
                        : EdgeRelationship.Inherits;

                    OtherEdges.Add(new GraphEdge
                    {
                        Source = nodeId,
                        Target = $"type:{baseTypeName}",
                        Relationship = relationship
                    });
                }
            }
        }

        private void AddContainmentEdges(string nodeId)
        {
            // File → Contains → Type
            OtherEdges.Add(new GraphEdge
            {
                Source = _relativePath,
                Target = nodeId,
                Relationship = EdgeRelationship.Contains
            });

            // Namespace → Contains → Type
            if (_currentNamespace is not null)
            {
                OtherEdges.Add(new GraphEdge
                {
                    Source = $"ns:{_currentNamespace}",
                    Target = nodeId,
                    Relationship = EdgeRelationship.Contains
                });
            }
        }

        /// <summary>
        /// Extracts the simple type name, stripping generic parameters and qualifiers.
        /// </summary>
        private static string GetBaseTypeName(TypeSyntax type)
        {
            return type switch
            {
                SimpleNameSyntax simple => simple.Identifier.Text,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                _ => type.ToString().Split('<')[0].Split('.')[^1].Trim()
            };
        }

        // ── Methods ───────────────────────────────────────────────

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (_currentType is not null)
            {
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                Symbols.Add(new SymbolInfo
                {
                    Name = node.Identifier.Text,
                    Kind = SymbolKind.Method,
                    FilePath = _relativePath,
                    Line = line,
                    ParentSymbol = _currentType
                });
            }

            base.VisitMethodDeclaration(node);
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            if (_currentType is not null)
            {
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                Symbols.Add(new SymbolInfo
                {
                    Name = node.Identifier.Text,
                    Kind = SymbolKind.Method,
                    FilePath = _relativePath,
                    Line = line,
                    ParentSymbol = _currentType
                });
            }

            base.VisitConstructorDeclaration(node);
        }

        // ── Properties ────────────────────────────────────────────

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (_currentType is not null)
            {
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                Symbols.Add(new SymbolInfo
                {
                    Name = node.Identifier.Text,
                    Kind = SymbolKind.Property,
                    FilePath = _relativePath,
                    Line = line,
                    ParentSymbol = _currentType
                });
            }

            base.VisitPropertyDeclaration(node);
        }
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
}
