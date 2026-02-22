namespace RepoLens.Shared.Models;

/// <summary>
/// High-level overview of an analyzed repository.
/// </summary>
public class RepositoryOverview
{
    public required string Name { get; set; }
    public required string Url { get; set; }
    public Dictionary<string, int> LanguageBreakdown { get; set; } = [];
    public Dictionary<string, int> LanguageLineBreakdown { get; set; } = [];
    public int TotalFiles { get; set; }
    public int TotalLines { get; set; }
    public List<string> DetectedFrameworks { get; set; } = [];
    public List<string> EntryPoints { get; set; } = [];
    public List<string> TopLevelFolders { get; set; } = [];

    // ─── Symbol-driven summary (Phase 4) ───────────────────────────

    /// <summary>Symbol counts keyed by kind name (Class, Interface, Method, Function, etc.).</summary>
    public Dictionary<string, int> SymbolCounts { get; set; } = [];

    /// <summary>Top types/classes ranked by member count.</summary>
    public List<KeyTypeInfo> KeyTypes { get; set; } = [];

    /// <summary>Modules/files with the most import connections.</summary>
    public List<ConnectedModuleInfo> MostConnectedModules { get; set; } = [];

    /// <summary>External (npm/NuGet) dependencies detected.</summary>
    public List<string> ExternalDependencies { get; set; } = [];

    /// <summary>Auto-generated plain-English summary of the repository.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Complexity label: Tiny, Small, Medium, Large, Huge.</summary>
    public string Complexity { get; set; } = "";
}

/// <summary>
/// A prominent type in the repository, ranked by member count.
/// </summary>
public class KeyTypeInfo
{
    public required string Name { get; set; }
    public required string FilePath { get; set; }
    public required string Kind { get; set; }
    public int MemberCount { get; set; }
}

/// <summary>
/// A module/file with many import connections.
/// </summary>
public class ConnectedModuleInfo
{
    public required string Name { get; set; }
    public required string FilePath { get; set; }
    public int IncomingEdges { get; set; }
    public int OutgoingEdges { get; set; }
}
