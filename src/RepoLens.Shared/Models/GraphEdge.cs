namespace RepoLens.Shared.Models;

/// <summary>
/// Represents a directed edge in the repository dependency graph.
/// </summary>
public class GraphEdge
{
    public required string Source { get; set; }
    public required string Target { get; set; }
    public EdgeRelationship Relationship { get; set; }
}

public enum EdgeRelationship
{
    Contains,
    Imports,
    Calls,
    Inherits,
    Implements
}
