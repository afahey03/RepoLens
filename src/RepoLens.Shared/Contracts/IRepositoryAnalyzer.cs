using RepoLens.Shared.Models;

namespace RepoLens.Shared.Contracts;

/// <summary>
/// Analyzes a repository's structure, symbols, and dependencies.
/// </summary>
public interface IRepositoryAnalyzer
{
    /// <summary>
    /// Scans all files in a repository directory.
    /// </summary>
    Task<List<Models.FileInfo>> ScanFilesAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts symbols from all supported files.
    /// </summary>
    Task<List<SymbolInfo>> ExtractSymbolsAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the full dependency graph for the repository.
    /// </summary>
    Task<DependencyGraph> BuildDependencyGraphAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a high-level overview of the repository.
    /// </summary>
    Task<RepositoryOverview> GenerateOverviewAsync(string repoPath, string repositoryUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a full analysis in a single pass — scans files once and reuses
    /// the result for overview, graph, and symbol extraction.
    /// </summary>
    Task<FullAnalysisResult> AnalyzeFullAsync(string repoPath, string repositoryUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Contains all results from a full repository analysis pass.
/// </summary>
public class FullAnalysisResult
{
    public required List<Models.FileInfo> Files { get; init; }
    public required List<SymbolInfo> Symbols { get; init; }
    public required DependencyGraph Graph { get; init; }
    public required RepositoryOverview Overview { get; init; }
}
