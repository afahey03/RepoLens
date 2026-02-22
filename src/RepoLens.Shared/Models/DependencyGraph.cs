namespace RepoLens.Shared.Models;

/// <summary>
/// The full dependency graph for a repository.
/// </summary>
public class DependencyGraph
{
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphEdge> Edges { get; set; } = [];
}
