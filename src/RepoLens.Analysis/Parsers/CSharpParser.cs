using RepoLens.Shared.Models;

namespace RepoLens.Analysis.Parsers;

/// <summary>
/// Parses C# files using simple text analysis.
/// Will be enhanced with Roslyn in Phase 3.
/// </summary>
public class CSharpParser : ILanguageParser
{
    public IReadOnlySet<string> SupportedLanguages { get; } = new HashSet<string> { "C#" };

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
