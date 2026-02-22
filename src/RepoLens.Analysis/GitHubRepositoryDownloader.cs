using RepoLens.Shared.Contracts;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis;

/// <summary>
/// Downloads a public GitHub repository as a zip archive and extracts it locally.
/// </summary>
public class GitHubRepositoryDownloader : IRepositoryDownloader
{
    private readonly HttpClient _httpClient;
    private readonly string _workingDirectory;

    public GitHubRepositoryDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _workingDirectory = Path.Combine(Path.GetTempPath(), "RepoLens");
        Directory.CreateDirectory(_workingDirectory);
    }

    public async Task<string> DownloadAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        // Parse owner/repo from URL
        var (owner, repo) = ParseGitHubUrl(repositoryUrl);
        var repoId = $"{owner}_{repo}";
        var targetDir = Path.Combine(_workingDirectory, repoId);

        // If already downloaded, reuse
        if (Directory.Exists(targetDir))
        {
            return targetDir;
        }

        // Download zip from GitHub
        var zipUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/main.zip";
        var zipPath = Path.Combine(_workingDirectory, $"{repoId}.zip");

        using var response = await _httpClient.GetAsync(zipUrl, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Try 'master' branch as fallback
            zipUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/master.zip";
            response.Dispose();
            using var fallbackResponse = await _httpClient.GetAsync(zipUrl, cancellationToken);
            fallbackResponse.EnsureSuccessStatusCode();

            await using var fallbackStream = await fallbackResponse.Content.ReadAsStreamAsync(cancellationToken);
            await using var fallbackFile = File.Create(zipPath);
            await fallbackStream.CopyToAsync(fallbackFile, cancellationToken);
        }
        else
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(zipPath);
            await stream.CopyToAsync(file, cancellationToken);
        }

        // Extract
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
        File.Delete(zipPath);

        // GitHub zips contain a single root folder like "repo-main/", find it
        var extractedDirs = Directory.GetDirectories(targetDir);
        if (extractedDirs.Length == 1)
        {
            return extractedDirs[0];
        }

        return targetDir;
    }

    private static (string Owner, string Repo) ParseGitHubUrl(string url)
    {
        // Support formats: https://github.com/owner/repo[.git]
        var uri = new Uri(url.TrimEnd('/'));
        var segments = uri.AbsolutePath.Trim('/').Split('/');

        if (segments.Length < 2)
        {
            throw new ArgumentException($"Invalid GitHub repository URL: {url}");
        }

        var owner = segments[0];
        var repo = segments[1].Replace(".git", "");

        return (owner, repo);
    }
}
