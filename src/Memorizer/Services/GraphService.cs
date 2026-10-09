using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Npgsql;
using Registrator.Net;

namespace Memorizer.Services;

/// <summary>
/// Provides the memory knowledge graph (nodes + relationship edges), optionally
/// scoped to a workspace subtree or a single project.
/// </summary>
[AutoRegisterInterfaces(ServiceLifetime.Scoped)]
public class GraphService : IGraphService
{
    private readonly NpgsqlDataSource _dataSource;

    public GraphService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<GraphData> GetKnowledgeGraphAsync(
        WorkspaceId? workspaceId = null,
        ProjectId? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId.HasValue && projectId.HasValue)
            throw new ArgumentException("workspaceId and projectId are mutually exclusive graph scopes.");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Owner scope for nodes. For a workspace this spans the entire subtree
        // (nested workspaces + all projects within them) via a recursive CTE.
        string ownerClause = "";
        var parameters = new List<(string Name, object Value)>();
        if (projectId.HasValue)
        {
            ownerClause = " AND owner_type = @projectType AND owner_id = @projectId";
            parameters.Add(("@projectType", (short)OwnerTypeEnum.Project));
            parameters.Add(("@projectId", projectId.Value.Value));
        }
        else if (workspaceId.HasValue)
        {
            ownerClause = @"
              AND (
                    (owner_type = @workspaceType AND owner_id IN (SELECT id FROM ws_tree))
                 OR (owner_type = @projectType AND owner_id IN (SELECT id FROM projects WHERE workspace_id IN (SELECT id FROM ws_tree)))
              )";
            parameters.Add(("@workspaceType", (short)OwnerTypeEnum.Workspace));
            parameters.Add(("@projectType", (short)OwnerTypeEnum.Project));
            parameters.Add(("@root", workspaceId.Value.Value));
        }

        string cte = workspaceId.HasValue
            ? @"WITH RECURSIVE ws_tree AS (
                    SELECT id FROM workspaces WHERE id = @root
                    UNION ALL
                    SELECT w.id FROM workspaces w JOIN ws_tree t ON w.parent_id = t.id
               ) "
            : "";

        // Nodes: all non-archived memories within scope.
        string nodeSql = $"{cte}SELECT id, COALESCE(NULLIF(title, ''), 'Untitled'), type_legacy FROM memories WHERE archetype IN (0, 1){ownerClause}";
        var nodes = new List<GraphNode>();
        await using (var nodeCmd = new NpgsqlCommand(nodeSql, connection))
        {
            foreach (var (name, value) in parameters)
                nodeCmd.Parameters.AddWithValue(name, value);

            await using var reader = await nodeCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                nodes.Add(new GraphNode
                {
                    Id = reader.GetGuid(0),
                    Title = reader.GetString(1),
                    Type = reader.IsDBNull(2) ? "" : reader.GetString(2)
                });
            }
        }

        if (nodes.Count == 0)
            return new GraphData();

        // Edges: relationships where both endpoints are non-archived.
        const string edgeSql = @"
            SELECT r.from_memory_id, r.to_memory_id, r.type
            FROM memory_relationships r
            JOIN memories f ON f.id = r.from_memory_id
            JOIN memories t ON t.id = r.to_memory_id
            WHERE f.archetype IN (0, 1) AND t.archetype IN (0, 1)";

        var edges = new List<GraphEdge>();
        await using (var edgeCmd = new NpgsqlCommand(edgeSql, connection))
        await using (var reader = await edgeCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                edges.Add(new GraphEdge
                {
                    From = reader.GetGuid(0),
                    To = reader.GetGuid(1),
                    Type = reader.GetString(2)
                });
            }
        }

        // Filter edges to those whose endpoints are in scope.
        var scopedNodeIds = nodes.Select(n => n.Id).ToHashSet();
        var scopedEdges = edges.Where(e => scopedNodeIds.Contains(e.From) && scopedNodeIds.Contains(e.To)).ToList();

        return new GraphData { Nodes = nodes, Edges = scopedEdges };
    }
}
