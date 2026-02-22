namespace RepoLens.Shared.Models;

/// <summary>
/// Represents a node in the repository dependency graph.
/// </summary>
public class GraphNode
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public NodeType Type { get; set; }
    public string? FilePath { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public enum NodeType
{
    Repository,
    Folder,
    File,
    Namespace,
    Class,
    Interface,
    Function,
    Module
}
