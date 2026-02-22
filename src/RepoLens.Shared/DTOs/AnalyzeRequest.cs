namespace RepoLens.Shared.DTOs;

/// <summary>
/// Request to analyze a public GitHub repository.
/// </summary>
public class AnalyzeRequest
{
    public required string RepositoryUrl { get; set; }
}
