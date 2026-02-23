namespace RepoLens.Shared.Contracts;

/// <summary>
/// Fetches the file-level diff for a GitHub Pull Request.
/// </summary>
public interface IPrDiffFetcher
{
    /// <summary>
    /// Returns a list of files changed in the given PR, along with patch/status info.
    /// </summary>
    Task<List<PrChangedFile>> FetchDiffAsync(
        string owner,
        string repo,
        int prNumber,
        string? gitHubToken = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A single file changed in a pull request.
/// </summary>
public class PrChangedFile
{
    /// <summary>File path relative to repo root (e.g. "src/Services/Foo.cs").</summary>
    public required string FilePath { get; set; }

    /// <summary>Status of the change: added, removed, modified, renamed.</summary>
    public required string Status { get; set; }

    /// <summary>Number of lines added.</summary>
    public int Additions { get; set; }

    /// <summary>Number of lines removed.</summary>
    public int Deletions { get; set; }

    /// <summary>Previous file path (for renames).</summary>
    public string? PreviousFilePath { get; set; }

    /// <summary>The unified diff patch text (may be null for binary files).</summary>
    public string? Patch { get; set; }
}
