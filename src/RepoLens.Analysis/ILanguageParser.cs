using RepoLens.Shared.Models;

namespace RepoLens.Analysis;

/// <summary>
/// Interface for language-specific parsers.
/// Each parser knows how to extract symbols and dependencies for a particular language.
/// </summary>
public interface ILanguageParser
{
    /// <summary>
    /// Languages supported by this parser (e.g., "C#", "TypeScript").
    /// </summary>
    IReadOnlySet<string> SupportedLanguages { get; }

    /// <summary>
    /// Extracts symbols (classes, functions, imports) from supported files.
    /// </summary>
    Task<List<SymbolInfo>> ExtractSymbolsAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds dependency nodes and edges from parsed files.
    /// </summary>
    Task<(List<GraphNode> Nodes, List<GraphEdge> Edges)> BuildDependenciesAsync(string repoPath, CancellationToken cancellationToken = default);
}
