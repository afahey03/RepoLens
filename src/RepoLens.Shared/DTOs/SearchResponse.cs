using RepoLens.Shared.Models;

namespace RepoLens.Shared.DTOs;

/// <summary>
/// Response containing search results with pagination and facets.
/// </summary>
public class SearchResponse
{
    public required string Query { get; set; }
    public int TotalResults { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
    public List<string> AvailableKinds { get; set; } = [];
    public List<SearchResult> Results { get; set; } = [];
}

/// <summary>
/// Lightweight suggestion for autocomplete.
/// </summary>
public class SearchSuggestionDto
{
    public required string Text { get; set; }
    public required string Kind { get; set; }
    public required string FilePath { get; set; }
}

/// <summary>
/// Response containing search suggestions.
/// </summary>
public class SuggestResponse
{
    public required string Prefix { get; set; }
    public List<SearchSuggestionDto> Suggestions { get; set; } = [];
}
