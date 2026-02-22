using RepoLens.Shared.Models;

namespace RepoLens.Shared.Contracts;

/// <summary>
/// Downloads a repository and provides access to its files.
/// </summary>
public interface IRepositoryDownloader
{
    /// <summary>
    /// Downloads a GitHub repository and returns the local path.
    /// Provide an optional <paramref name="gitHubToken"/> (Personal Access Token)
    /// to access private or organization repositories.
    /// </summary>
    Task<string> DownloadAsync(string repositoryUrl, CancellationToken cancellationToken = default, string? gitHubToken = null);

    /// <summary>
    /// Removes the locally cached download for a repository so the next
    /// <see cref="DownloadAsync"/> call fetches fresh content.
    /// </summary>
    void ClearLocalCache(string repositoryUrl);
}
