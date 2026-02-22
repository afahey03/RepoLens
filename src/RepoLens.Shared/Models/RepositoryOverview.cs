namespace RepoLens.Shared.Models;

/// <summary>
/// High-level overview of an analyzed repository.
/// </summary>
public class RepositoryOverview
{
    public required string Name { get; set; }
    public required string Url { get; set; }
    public Dictionary<string, int> LanguageBreakdown { get; set; } = [];
    public int TotalFiles { get; set; }
    public int TotalLines { get; set; }
    public List<string> DetectedFrameworks { get; set; } = [];
    public List<string> EntryPoints { get; set; } = [];
    public List<string> TopLevelFolders { get; set; } = [];
}
