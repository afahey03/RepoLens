using RepoLens.Shared.Models;

namespace RepoLens.Shared.Contracts;

/// <summary>
/// Stores and retrieves analysis results with disk persistence.
/// </summary>
public interface IAnalysisCache
{
    /// <summary>
    /// Returns true if a cached analysis exists for the given repository ID.
    /// </summary>
    bool Has(string repositoryId);

    /// <summary>
    /// Stores an analysis result set for the given repository ID.
    /// Persists to disk for survival across restarts.
    /// </summary>
    void Store(string repositoryId, CachedAnalysis analysis);

    /// <summary>
    /// Retrieves a cached analysis. Returns null if not found.
    /// </summary>
    CachedAnalysis? Get(string repositoryId);

    /// <summary>
    /// Removes a cached analysis from memory and disk.
    /// </summary>
    void Remove(string repositoryId);

    /// <summary>
    /// Returns all cached repository IDs.
    /// </summary>
    IReadOnlyList<string> GetCachedIds();
}

/// <summary>
/// Data stored per repository analysis.
/// </summary>
public class CachedAnalysis
{
    public required RepositoryOverview Overview { get; init; }
    public required DependencyGraph Graph { get; init; }
    public required List<SymbolInfo> Symbols { get; init; }
    public required List<Models.FileInfo> Files { get; init; }
    public DateTime AnalyzedAtUtc { get; init; } = DateTime.UtcNow;
}
