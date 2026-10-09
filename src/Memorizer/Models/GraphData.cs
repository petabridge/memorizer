namespace Memorizer.Models;

/// <summary>
/// Full snapshot of the memory knowledge graph: nodes (memories) and edges (relationships).
/// </summary>
public class GraphData
{
    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphEdge> Edges { get; set; } = new();
}

public class GraphNode
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "Untitled";
    public string Type { get; init; } = "";
}

public class GraphEdge
{
    public Guid From { get; init; }
    public Guid To { get; init; }
    public string Type { get; init; } = "";
}
