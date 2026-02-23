using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Contracts;

namespace RepoLens.Analysis;

/// <summary>
/// Fetches PR file diffs from the GitHub REST API (v3).
/// Supports pagination for PRs with > 30 changed files.
/// </summary>
public class GitHubPrDiffFetcher : IPrDiffFetcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubPrDiffFetcher> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GitHubPrDiffFetcher(HttpClient httpClient, ILogger<GitHubPrDiffFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<PrChangedFile>> FetchDiffAsync(
        string owner,
        string repo,
        int prNumber,
        string? gitHubToken = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching PR #{PrNumber} diff for {Owner}/{Repo}", prNumber, owner, repo);

        var allFiles = new List<PrChangedFile>();
        var page = 1;
        const int perPage = 100;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"https://api.github.com/repos/{owner}/{repo}/pulls/{prNumber}/files?per_page={perPage}&page={page}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RepoLens", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            if (!string.IsNullOrWhiteSpace(gitHubToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("GitHub API returned {StatusCode} for PR #{PrNumber}: {Body}",
                    (int)response.StatusCode, prNumber, body);
                throw new HttpRequestException(
                    $"GitHub API returned {(int)response.StatusCode} for PR #{prNumber}. {body}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var files = JsonSerializer.Deserialize<List<GitHubPrFile>>(json, JsonOptions);

            if (files is null || files.Count == 0)
                break;

            foreach (var f in files)
            {
                allFiles.Add(new PrChangedFile
                {
                    FilePath = f.Filename ?? "",
                    Status = f.Status ?? "modified",
                    Additions = f.Additions,
                    Deletions = f.Deletions,
                    PreviousFilePath = f.PreviousFilename,
                    Patch = f.Patch
                });
            }

            if (files.Count < perPage)
                break;

            page++;
        }

        _logger.LogInformation("PR #{PrNumber}: fetched {Count} changed files", prNumber, allFiles.Count);
        return allFiles;
    }

    /// <summary>
    /// Maps the GitHub API JSON response for PR files.
    /// </summary>
    private class GitHubPrFile
    {
        public string? Filename { get; set; }
        public string? Status { get; set; }
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public string? PreviousFilename { get; set; }
        public string? Patch { get; set; }
    }
}
