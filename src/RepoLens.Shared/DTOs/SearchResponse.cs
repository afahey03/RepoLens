using RepoLens.Shared.Models;

namespace RepoLens.Shared.DTOs;

/// <summary>
/// Response containing search results.
/// </summary>
public class SearchResponse
{
    public required string Query { get; set; }
    public int TotalResults { get; set; }
    public List<SearchResult> Results { get; set; } = [];
}
