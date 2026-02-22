namespace RepoLens.Shared.Models;

/// <summary>
/// A single search result from the code index.
/// </summary>
public class SearchResult
{
    public required string FilePath { get; set; }
    public string? Symbol { get; set; }
    public required string Snippet { get; set; }
    public double Score { get; set; }
    public int Line { get; set; }
    public string Kind { get; set; } = "File";
}
