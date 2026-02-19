using Memorizer.Models;
using Memorizer.Models.ValueTypes;

namespace Memorizer.Services;

/// <summary>
/// Service for generating canonical URLs to resources in the Memorizer web UI.
/// </summary>
public interface ICanonicalUrlService
{
    /// <summary>
    /// Gets the canonical URL for a memory.
    /// </summary>
    /// <param name="memoryId">The memory ID</param>
    /// <returns>The canonical URL, or null if not configured</returns>
    string? GetMemoryUrl(MemoryId memoryId);

    /// <summary>
    /// Gets the canonical URL for a workspace.
    /// </summary>
    /// <param name="workspaceId">The workspace ID</param>
    /// <returns>The canonical URL, or null if not configured</returns>
    string? GetWorkspaceUrl(WorkspaceId workspaceId);

    /// <summary>
    /// Gets the canonical URL for a project.
    /// </summary>
    /// <param name="projectId">The project ID</param>
    /// <returns>The canonical URL, or null if not configured</returns>
    string? GetProjectUrl(ProjectId projectId);

    /// <summary>
    /// Returns true if the service is configured and can generate URLs.
    /// </summary>
    bool IsConfigured { get; }
}
