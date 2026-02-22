using RepoLens.Shared.Models;

namespace RepoLens.Shared.Contracts;

/// <summary>
/// Downloads a repository and provides access to its files.
/// </summary>
public interface IRepositoryDownloader
{
    /// <summary>
    /// Downloads a public GitHub repository and returns the local path.
    /// </summary>
    Task<string> DownloadAsync(string repositoryUrl, CancellationToken cancellationToken = default);
}
