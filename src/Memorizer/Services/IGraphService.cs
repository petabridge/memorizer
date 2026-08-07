using Memorizer.Models;
using Memorizer.Models.ValueTypes;

namespace Memorizer.Services;

/// <summary>
/// Provides the memory knowledge graph (nodes + relationship edges), optionally
/// scoped to a workspace subtree or a single project.
/// </summary>
public interface IGraphService
{
    /// <summary>
    /// Gets all non-archived memories as graph nodes and their relationships as edges.
    /// </summary>
    /// <param name="workspaceId">When set, restricts the graph to the workspace subtree
    /// (direct memories + projects + nested workspaces). Mutually exclusive with projectId.</param>
    /// <param name="projectId">When set, restricts the graph to memories owned by this project.</param>
    Task<GraphData> GetKnowledgeGraphAsync(
        WorkspaceId? workspaceId = null,
        ProjectId? projectId = null,
        CancellationToken cancellationToken = default);
}
