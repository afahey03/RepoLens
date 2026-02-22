namespace RepoLens.Shared.Models;

/// <summary>
/// Tracks the progress of a repository analysis in real time.
/// </summary>
public class AnalysisProgress
{
    public string RepositoryId { get; set; } = "";
    public AnalysisStage Stage { get; set; } = AnalysisStage.Queued;
    public string StageLabel { get; set; } = "Queued";
    public int PercentComplete { get; set; }
    public string? Error { get; set; }
}

public enum AnalysisStage
{
    Queued,
    Downloading,
    Scanning,
    Parsing,
    BuildingGraph,
    Indexing,
    Completed,
    Failed
}
