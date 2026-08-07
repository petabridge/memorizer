using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Npgsql;
using Registrator.Net;

namespace Memorizer.Services;

/// <summary>
/// Computes tag clouds (tag → memory count) scoped to workspaces and projects.
/// </summary>
[AutoRegisterInterfaces(ServiceLifetime.Scoped)]
public class TagCloudService : ITagCloudService
{
    private readonly NpgsqlDataSource _dataSource;

    public TagCloudService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<List<TagCount>> GetWorkspaceSubtreeTagCountsAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            WITH RECURSIVE ws_tree AS (
                SELECT id FROM workspaces WHERE id = @root
                UNION ALL
                SELECT w.id FROM workspaces w JOIN ws_tree t ON w.parent_id = t.id
            )
            SELECT tag, COUNT(*) AS cnt
            FROM memories, unnest(tags) AS tag
            WHERE archetype IN (0, 1)
              AND (
                    (owner_type = @workspaceType AND owner_id IN (SELECT id FROM ws_tree))
                 OR (owner_type = @projectType AND owner_id IN (
                        SELECT id FROM projects WHERE workspace_id IN (SELECT id FROM ws_tree)))
              )
            GROUP BY tag
            ORDER BY cnt DESC, tag";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("root", workspaceId.Value);
        command.Parameters.AddWithValue("workspaceType", (short)OwnerTypeEnum.Workspace);
        command.Parameters.AddWithValue("projectType", (short)OwnerTypeEnum.Project);

        return await ReadTagCountsAsync(command, cancellationToken);
    }

    public async Task<List<TagCount>> GetProjectTagCountsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT tag, COUNT(*) AS cnt
            FROM memories, unnest(tags) AS tag
            WHERE archetype IN (0, 1)
              AND owner_type = @projectType
              AND owner_id = @projectId
            GROUP BY tag
            ORDER BY cnt DESC, tag";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projectId", projectId.Value);
        command.Parameters.AddWithValue("projectType", (short)OwnerTypeEnum.Project);

        return await ReadTagCountsAsync(command, cancellationToken);
    }

    private static async Task<List<TagCount>> ReadTagCountsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<TagCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TagCount
            {
                Tag = reader.GetString(0),
                Count = reader.GetInt32(1)
            });
        }
        return result;
    }
}
