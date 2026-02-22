namespace RepoLens.Shared.DTOs;

/// <summary>
/// Response returned after triggering repository analysis.
/// </summary>
public class AnalyzeResponse
{
    public required string RepositoryId { get; set; }
    public required string Status { get; set; }
}
