using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Microsoft.AspNetCore.Mvc;

namespace Memorizer.Controllers;

/// <summary>
/// REST API controller for workspace management
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class WorkspaceController : ControllerBase
{
    private readonly IStorage _storage;

    public WorkspaceController(IStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Get all workspaces with optional system workspace inclusion
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<WorkspaceListResponse>> GetWorkspaces(
        [FromQuery] bool includeSystem = true,
        CancellationToken cancellationToken = default)
    {
        var workspaces = await _storage.GetWorkspacesAsync(parentId: null, includeSystem, cancellationToken);

        // Get counts for each workspace
        var workspaceDtos = new List<WorkspaceDto>();
        foreach (var ws in workspaces)
        {
            var (totalMemoryCount, projectCount) = await GetWorkspaceTotalMemoryCountAsync(ws.Id, cancellationToken);

            workspaceDtos.Add(new WorkspaceDto
            {
                Id = ws.Id.Value,
                Name = ws.Name,
                Slug = ws.Slug,
                Description = ws.Description,
                IsSystem = ws.IsSystem,
                MemoryCount = totalMemoryCount,
                ProjectCount = projectCount,
                CreatedAt = ws.CreatedAt,
                UpdatedAt = ws.UpdatedAt
            });
        }

        return Ok(new WorkspaceListResponse { Workspaces = workspaceDtos });
    }

    /// <summary>
    /// Get a specific workspace by ID with projects and recent memories
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkspaceDetailDto>> GetWorkspace(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = new WorkspaceId(id);
        var workspace = await _storage.GetWorkspaceAsync(workspaceId, cancellationToken);

        if (workspace == null)
        {
            return NotFound(new { message = $"Workspace with ID {id} not found" });
        }

        var projects = await _storage.GetProjectsAsync(workspaceId, parentId: null, statusFilter: null, cancellationToken);

        // Get project DTOs with memory counts and calculate total
        var projectDtos = new List<ProjectSummaryDto>();
        var totalProjectMemories = 0;
        foreach (var proj in projects)
        {
            var projMemoryCount = await _storage.GetMemoryCountByOwnerAsync(
                MemoryOwner.ForProject(proj.Id), cancellationToken);
            totalProjectMemories += projMemoryCount;
            projectDtos.Add(new ProjectSummaryDto
            {
                Id = proj.Id.Value,
                Name = proj.Name,
                Slug = proj.Slug,
                Description = proj.Description,
                Status = proj.Status.ToString().ToLowerInvariant(),
                MemoryCount = projMemoryCount
            });
        }

        // Get memories directly in this workspace (not in projects)
        var directMemoryCount = await _storage.GetMemoryCountByOwnerAsync(
            MemoryOwner.ForWorkspace(workspaceId), cancellationToken);

        // Total includes workspace memories + all project memories
        var totalMemoryCount = directMemoryCount + totalProjectMemories;

        // Get recent memories directly in this workspace
        var recentMemories = await _storage.GetMemoriesByOwnerAsync(
            MemoryOwner.ForWorkspace(workspaceId), 1, 5, cancellationToken);

        // Get child workspaces (nested workspaces)
        var childWorkspaces = await _storage.GetWorkspacesAsync(parentId: workspaceId, includeSystem: false, cancellationToken);
        var childWorkspaceDtos = new List<WorkspaceSummaryDto>();
        foreach (var child in childWorkspaces)
        {
            var (childMemoryCount, childProjectCount) = await GetWorkspaceTotalMemoryCountAsync(child.Id, cancellationToken);
            childWorkspaceDtos.Add(new WorkspaceSummaryDto
            {
                Id = child.Id.Value,
                Name = child.Name,
                Slug = child.Slug,
                Description = child.Description,
                MemoryCount = childMemoryCount,
                ProjectCount = childProjectCount
            });
        }

        return Ok(new WorkspaceDetailDto
        {
            Id = workspace.Id.Value,
            Name = workspace.Name,
            Slug = workspace.Slug,
            Description = workspace.Description,
            IsSystem = workspace.IsSystem,
            CreatedAt = workspace.CreatedAt,
            UpdatedAt = workspace.UpdatedAt,
            MemoryCount = totalMemoryCount,
            Projects = projectDtos,
            RecentMemories = recentMemories.Select(m => new MemorySummaryDto
            {
                Id = m.Id.Value,
                Title = m.Title ?? "Untitled",
                Type = m.Type,
                Archetype = m.Archetype.ToStringValue()
            }).ToList(),
            ChildWorkspaces = childWorkspaceDtos
        });
    }

    /// <summary>
    /// Create a new workspace
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<WorkspaceDto>> CreateWorkspace(
        [FromBody] CreateWorkspaceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Name is required" });
        }

        try
        {
            WorkspaceId? parentId = request.ParentId.HasValue
                ? new WorkspaceId(request.ParentId.Value)
                : null;

            var workspace = await _storage.CreateWorkspaceAsync(
                request.Name,
                request.Description,
                parentId,
                cancellationToken);

            return CreatedAtAction(
                nameof(GetWorkspace),
                new { id = workspace.Id.Value },
                new WorkspaceDto
                {
                    Id = workspace.Id.Value,
                    Name = workspace.Name,
                    Slug = workspace.Slug,
                    Description = workspace.Description,
                    IsSystem = workspace.IsSystem,
                    MemoryCount = 0,
                    ProjectCount = 0,
                    CreatedAt = workspace.CreatedAt,
                    UpdatedAt = workspace.UpdatedAt
                });
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { message = $"A workspace with the name '{request.Name}' already exists" });
        }
    }

    /// <summary>
    /// Update a workspace
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WorkspaceDto>> UpdateWorkspace(
        Guid id,
        [FromBody] UpdateWorkspaceRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = new WorkspaceId(id);
        var existing = await _storage.GetWorkspaceAsync(workspaceId, cancellationToken);

        if (existing == null)
        {
            return NotFound(new { message = $"Workspace with ID {id} not found" });
        }

        if (existing.IsSystem)
        {
            return BadRequest(new { message = "System workspaces cannot be modified" });
        }

        try
        {
            WorkspaceId? newParentId = request.NewParentId.HasValue
                ? new WorkspaceId(request.NewParentId.Value)
                : null;

            var updated = await _storage.UpdateWorkspaceAsync(
                workspaceId,
                request.Name,
                request.Description,
                newParentId,
                request.MakeTopLevel,
                cancellationToken);

            var memoryCount = await _storage.GetMemoryCountByOwnerAsync(
                MemoryOwner.ForWorkspace(workspaceId), cancellationToken);
            var projects = await _storage.GetProjectsAsync(workspaceId, parentId: null, statusFilter: null, cancellationToken);

            return Ok(new WorkspaceDto
            {
                Id = updated.Id.Value,
                Name = updated.Name,
                Slug = updated.Slug,
                Description = updated.Description,
                IsSystem = updated.IsSystem,
                MemoryCount = memoryCount,
                ProjectCount = projects.Count,
                CreatedAt = updated.CreatedAt,
                UpdatedAt = updated.UpdatedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { message = $"A workspace with this name already exists at the target level. Rename the workspace before moving." });
        }
    }

    /// <summary>
    /// Delete a workspace
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteWorkspace(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = new WorkspaceId(id);
        var existing = await _storage.GetWorkspaceAsync(workspaceId, cancellationToken);

        if (existing == null)
        {
            return NotFound(new { message = $"Workspace with ID {id} not found" });
        }

        if (existing.IsSystem)
        {
            return BadRequest(new { message = "System workspaces cannot be deleted" });
        }

        // Get counts for warning
        var memoryCount = await _storage.GetMemoryCountByOwnerAsync(
            MemoryOwner.ForWorkspace(workspaceId), cancellationToken);
        var projects = await _storage.GetProjectsAsync(workspaceId, parentId: null, statusFilter: null, cancellationToken);

        await _storage.DeleteWorkspaceAsync(workspaceId, cancellationToken);

        return Ok(new {
            message = $"Workspace '{existing.Name}' deleted",
            memoriesMoved = memoryCount,
            projectsDeleted = projects.Count
        });
    }

    /// <summary>
    /// Calculate total memory count for a workspace, including memories in all projects
    /// </summary>
    private async Task<(int TotalMemoryCount, int ProjectCount)> GetWorkspaceTotalMemoryCountAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        // Get memories directly in the workspace
        var directMemoryCount = await _storage.GetMemoryCountByOwnerAsync(
            MemoryOwner.ForWorkspace(workspaceId), cancellationToken);

        // Get all projects in the workspace
        var projects = await _storage.GetProjectsAsync(workspaceId, parentId: null, statusFilter: null, cancellationToken);

        // Sum up memories in all projects
        var projectMemoryCount = 0;
        foreach (var project in projects)
        {
            var projMemCount = await _storage.GetMemoryCountByOwnerAsync(
                MemoryOwner.ForProject(project.Id), cancellationToken);
            projectMemoryCount += projMemCount;
        }

        return (directMemoryCount + projectMemoryCount, projects.Count);
    }
}

// Request/Response DTOs

public class WorkspaceListResponse
{
    public List<WorkspaceDto> Workspaces { get; set; } = new();
}

public class WorkspaceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public int MemoryCount { get; set; }
    public int ProjectCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class WorkspaceDetailDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MemoryCount { get; set; }
    public List<ProjectSummaryDto> Projects { get; set; } = new();
    public List<MemorySummaryDto> RecentMemories { get; set; } = new();
    public List<WorkspaceSummaryDto> ChildWorkspaces { get; set; } = new();
}

public class WorkspaceSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public int MemoryCount { get; set; }
    public int ProjectCount { get; set; }
}

public class ProjectSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "draft";
    public int MemoryCount { get; set; }
}

public class MemorySummaryDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string Archetype { get; set; } = "document";
}

public class CreateWorkspaceRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? ParentId { get; set; }
}

public class UpdateWorkspaceRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Guid? NewParentId { get; set; }
    public bool MakeTopLevel { get; set; }
}
