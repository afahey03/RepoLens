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

    /// <summary>
    /// Runs an incremental analysis — re-parses only files that have changed
    /// (based on SHA-256 content hashes) since the previous analysis.
    /// Returns a full result set that merges unchanged cached data with fresh data for changed files.
    /// </summary>
    Task<FullAnalysisResult> AnalyzeIncrementalAsync(
        string repoPath, string repositoryUrl,
        CachedAnalysis previousAnalysis,
        CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Content of the repository's README file (if found), used for LLM summary enrichment.
    /// </summary>
    public string? ReadmeContent { get; init; }
}
