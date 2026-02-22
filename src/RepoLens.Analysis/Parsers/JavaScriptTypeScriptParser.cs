using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses JavaScript and TypeScript files using simple text analysis.
/// Will be enhanced with proper AST parsing in later phases.
/// </summary>
public class JavaScriptTypeScriptParser : ILanguageParser
{
    public IReadOnlySet<string> SupportedLanguages { get; } = new HashSet<string> { "JavaScript", "TypeScript" };

    public Task<List<SymbolInfo>> ExtractSymbolsAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        // Stub — will be implemented in Phase 3
        return Task.FromResult(new List<SymbolInfo>());
    }

    public Task<(List<GraphNode> Nodes, List<GraphEdge> Edges)> BuildDependenciesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        // Stub — will be implemented in Phase 3
        return Task.FromResult((new List<GraphNode>(), new List<GraphEdge>()));
    }
}
