namespace RepoLens.Shared.DTOs;

/// <summary>
/// Request to analyze the impact of a pull request on a previously-analyzed repository.
/// </summary>
public class PrImpactRequest
{
    /// <summary>
    /// The PR number (e.g. 42).
    /// </summary>
    public int PrNumber { get; set; }

    /// <summary>
    /// Optional GitHub Personal Access Token for private repos or higher rate limits.
    /// </summary>
    public string? GitHubToken { get; set; }
}

/// <summary>
/// The full impact analysis result for a pull request.
/// </summary>
public class PrImpactResponse
{
    /// <summary>PR number that was analyzed.</summary>
    public int PrNumber { get; set; }

    /// <summary>Total number of files changed in the PR.</summary>
    public int TotalFilesChanged { get; set; }

    /// <summary>Total lines added across all files.</summary>
    public int TotalAdditions { get; set; }

    /// <summary>Total lines removed across all files.</summary>
    public int TotalDeletions { get; set; }

    /// <summary>Files changed in the PR with their status and line-level impact.</summary>
    public List<PrFileImpact> ChangedFiles { get; set; } = [];

    /// <summary>Symbols (classes, methods, etc.) defined in the changed files.</summary>
    public List<PrSymbolImpact> AffectedSymbols { get; set; } = [];

    /// <summary>Graph edges that originate or terminate at changed files/symbols.</summary>
    public List<PrEdgeImpact> AffectedEdges { get; set; } = [];

    /// <summary>Files that import/depend on the changed files (ripple effect).</summary>
    public List<string> DownstreamFiles { get; set; } = [];

    /// <summary>Languages touched by this PR.</summary>
    public List<string> LanguagesTouched { get; set; } = [];
}

/// <summary>
/// Impact summary for a single changed file.
/// </summary>
public class PrFileImpact
{
    public required string FilePath { get; set; }
    public required string Status { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public string? Language { get; set; }
    public string? PreviousFilePath { get; set; }

    /// <summary>Number of symbols defined in this file.</summary>
    public int SymbolCount { get; set; }
}

/// <summary>
/// A symbol defined in a changed file.
/// </summary>
public class PrSymbolImpact
{
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public required string FilePath { get; set; }
    public int Line { get; set; }
    public string? ParentSymbol { get; set; }
}

/// <summary>
/// A graph edge connected to a changed file or symbol.
/// </summary>
public class PrEdgeImpact
{
    public required string Source { get; set; }
    public required string Target { get; set; }
    public required string Relationship { get; set; }

    /// <summary>Whether the source or target is the directly-changed node.</summary>
    public required string ImpactSide { get; set; }
}
