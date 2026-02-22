namespace RepoLens.Shared.DTOs;

/// <summary>
/// Request to analyze a GitHub repository.
/// Provide an optional <see cref="GitHubToken"/> to access private repos.
/// </summary>
public class AnalyzeRequest
{
    public required string RepositoryUrl { get; set; }

    /// <summary>
    /// Optional GitHub Personal Access Token for private/org repositories.
    /// Never persisted to disk — used only for the download request.
    /// </summary>
    public string? GitHubToken { get; set; }
}
