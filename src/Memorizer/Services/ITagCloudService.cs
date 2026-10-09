using Memorizer.Models;
using Memorizer.Models.ValueTypes;

namespace Memorizer.Services;

/// <summary>
/// Computes tag clouds (tag → memory count) scoped to workspaces and projects.
/// </summary>
public interface ITagCloudService
{
    /// <summary>
    /// Gets tag counts across the entire workspace subtree: direct workspace
    /// memories, all projects, and all nested workspaces (recursive).
    /// </summary>
    Task<List<TagCount>> GetWorkspaceSubtreeTagCountsAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets tag counts for memories directly owned by a single project.
    /// </summary>
    Task<List<TagCount>> GetProjectTagCountsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets tag counts across all non-archived memories (no owner scope).
    /// </summary>
    Task<List<TagCount>> GetGlobalTagCountsAsync(CancellationToken cancellationToken = default);
}
