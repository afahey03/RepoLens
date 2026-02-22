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

    /// <summary>
    /// Optional OpenAI API key for LLM-powered summary generation.
    /// If omitted, falls back to the server-side REPOLENS_OPENAI_API_KEY env var.
    /// Never persisted to disk.
    /// </summary>
    public string? OpenAiApiKey { get; set; }

    /// <summary>
    /// When true, forces a fresh download and incremental re-analysis
    /// even if a cached result already exists.
    /// </summary>
    public bool ForceReanalyze { get; set; }
}
