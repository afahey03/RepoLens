using RepoLens.Shared.Models;

namespace RepoLens.Shared.DTOs;

/// <summary>
/// Response containing the architecture graph for a repository.
/// </summary>
public class ArchitectureResponse
{
    public List<GraphNodeDto> Nodes { get; set; } = [];
    public List<GraphEdgeDto> Edges { get; set; } = [];
}

/// <summary>
/// Aggregate statistics about the architecture graph.
/// </summary>
public class GraphStatsResponse
{
    public int TotalNodes { get; set; }
    public int TotalEdges { get; set; }
    public Dictionary<string, int> NodeTypeCounts { get; set; } = [];
    public Dictionary<string, int> EdgeTypeCounts { get; set; } = [];
    public int MaxDepth { get; set; }
    public List<string> RootNodes { get; set; } = [];
    public List<string> LeafNodes { get; set; } = [];
}

public class GraphNodeDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public class GraphEdgeDto
{
    public required string Source { get; set; }
    public required string Target { get; set; }
    public required string Relationship { get; set; }
}
