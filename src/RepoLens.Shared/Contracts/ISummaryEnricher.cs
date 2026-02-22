using RepoLens.Shared.Models;

namespace RepoLens.Shared.Contracts;

/// <summary>
/// Optionally enriches a repository summary using an LLM.
/// Returns null if no API key is configured or the call fails,
/// allowing the caller to fall back to the template summary.
/// </summary>
public interface ISummaryEnricher
{
    /// <summary>
    /// Generates an LLM-powered summary of the repository.
    /// <paramref name="apiKey"/> overrides any server-side configured key.
    /// Returns null when the enrichment is unavailable or fails.
    /// </summary>
    Task<string?> EnrichAsync(
        RepositoryOverview overview,
        string? readmeContent,
        string? apiKey = null,
        CancellationToken cancellationToken = default);
}
