namespace RepoLens.Shared.Models;

/// <summary>
/// Metadata about a scanned file in the repository.
/// </summary>
public class FileInfo
{
    public required string RelativePath { get; set; }
    public required string Language { get; set; }
    public long SizeBytes { get; set; }
    public int LineCount { get; set; }

    /// <summary>
    /// SHA-256 hash of the file contents. Used for incremental re-analysis
    /// to detect which files have changed since the last analysis.
    /// </summary>
    public string? ContentHash { get; set; }
}
