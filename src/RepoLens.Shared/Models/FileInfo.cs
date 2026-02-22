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
}
