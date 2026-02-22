using System.IO.Compression;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Contracts;

namespace RepoLens.Analysis;

/// <summary>
/// Downloads a public GitHub repository as a zip archive and extracts it locally.
/// </summary>
public class GitHubRepositoryDownloader : IRepositoryDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubRepositoryDownloader> _logger;
    private readonly string _workingDirectory;

    private static readonly string[] BranchCandidates = ["main", "master"];

    /// <summary>Max retries for transient download failures.</summary>
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(6)
    ];

    public GitHubRepositoryDownloader(HttpClient httpClient, ILogger<GitHubRepositoryDownloader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _workingDirectory = Path.Combine(Path.GetTempPath(), "RepoLens");
        Directory.CreateDirectory(_workingDirectory);
    }

    public async Task<string> DownloadAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var (owner, repo) = ParseGitHubUrl(repositoryUrl);
        var repoId = $"{owner}_{repo}";
        var targetDir = Path.Combine(_workingDirectory, repoId);

        // If already downloaded, return the inner extracted folder
        if (Directory.Exists(targetDir))
        {
            _logger.LogInformation("Repository {Owner}/{Repo} already cached at {Path}", owner, repo, targetDir);
            return ResolveExtractedRoot(targetDir);
        }

        _logger.LogInformation("Downloading repository {Owner}/{Repo}...", owner, repo);

        var zipPath = Path.Combine(_workingDirectory, $"{repoId}.zip");

        try
        {
            await DownloadZipAsync(owner, repo, zipPath, cancellationToken);
            ExtractZip(zipPath, targetDir);
        }
        finally
        {
            // Always clean up the zip file
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }

        var result = ResolveExtractedRoot(targetDir);
        _logger.LogInformation("Repository {Owner}/{Repo} ready at {Path}", owner, repo, result);
        return result;
    }

    private async Task DownloadZipAsync(string owner, string repo, string zipPath, CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                _logger.LogWarning("Retry {Attempt}/{Max} for {Owner}/{Repo} after {Delay}s...",
                    attempt, MaxRetries, owner, repo, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }

            foreach (var branch in BranchCandidates)
            {
                var zipUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/{branch}.zip";
                _logger.LogDebug("Trying {Url}...", zipUrl);

                try
                {
                    using var response = await _httpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("Branch '{Branch}' returned {StatusCode}, trying next...", branch, response.StatusCode);
                        continue;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var file = File.Create(zipPath);
                    await stream.CopyToAsync(file, cancellationToken);

                    _logger.LogInformation("Downloaded {Owner}/{Repo} from branch '{Branch}' ({Size:N0} bytes)",
                        owner, repo, branch, new System.IO.FileInfo(zipPath).Length);
                    return;
                }
                catch (HttpRequestException ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Transient HTTP error downloading {Owner}/{Repo} branch '{Branch}'",
                        owner, repo, branch);
                    break; // Break inner loop to retry from first branch
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogWarning("Timeout downloading {Owner}/{Repo} branch '{Branch}', will retry",
                        owner, repo, branch);
                    break;
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not download repository {owner}/{repo} after {MaxRetries + 1} attempts. " +
            $"Tried branches: {string.Join(", ", BranchCandidates)}. " +
            "Ensure the repository is public and the URL is correct.",
            lastException);
    }

    private void ExtractZip(string zipPath, string targetDir)
    {
        _logger.LogDebug("Extracting {Zip} to {Dir}...", zipPath, targetDir);
        ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
    }

    /// <summary>
    /// GitHub zips contain a single root folder (e.g. "repo-main/"). Return that folder.
    /// </summary>
    private static string ResolveExtractedRoot(string targetDir)
    {
        var dirs = Directory.GetDirectories(targetDir);
        return dirs.Length == 1 ? dirs[0] : targetDir;
    }

    private static (string Owner, string Repo) ParseGitHubUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Repository URL cannot be empty.");

        // Normalize
        url = url.Trim().TrimEnd('/');

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid URL format: {url}");

        if (uri.Host is not ("github.com" or "www.github.com"))
            throw new ArgumentException($"Only GitHub URLs are supported. Got: {uri.Host}");

        var segments = uri.AbsolutePath.Trim('/').Split('/');

        if (segments.Length < 2 || string.IsNullOrEmpty(segments[0]) || string.IsNullOrEmpty(segments[1]))
            throw new ArgumentException($"Could not parse owner/repo from URL: {url}");

        var owner = segments[0];
        var repo = segments[1].Replace(".git", "", StringComparison.OrdinalIgnoreCase);

        return (owner, repo);
    }
}
