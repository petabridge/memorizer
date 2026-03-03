using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Microsoft.AspNetCore.Mvc;

namespace Memorizer.Controllers;

/// <summary>
/// REST API controller for project management
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ProjectController : ControllerBase
{
    private readonly IStorage _storage;

    public ProjectController(IStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Get projects, optionally filtered by workspace
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ProjectListResponse>> GetProjects(
        [FromQuery] Guid? workspaceId = null,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        // Parse status filter
        ProjectStatusEnum? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status) && status.ToLowerInvariant() != "all")
        {
            statusFilter = ProjectStatusEnumExtensions.ParseProjectStatus(status);
        }

        var projectDtos = new List<ProjectDto>();

        if (workspaceId.HasValue)
        {
            var typedWorkspaceId = new WorkspaceId(workspaceId.Value);
            var workspace = await _storage.GetWorkspaceAsync(typedWorkspaceId, cancellationToken);
            if (workspace == null)
            {
                return NotFound(new { message = $"Workspace with ID {workspaceId} not found" });
            }

            var projects = await _storage.GetProjectsAsync(typedWorkspaceId, parentId: null, statusFilter, cancellationToken);
            foreach (var proj in projects)
            {
                projectDtos.Add(await ToProjectDto(proj, workspace.Name, cancellationToken));
            }
        }
        else
        {
            // Get all projects across all workspaces
            var workspaces = await _storage.GetWorkspacesAsync(parentId: null, includeSystem: false, cancellationToken);
            foreach (var ws in workspaces)
            {
                var projects = await _storage.GetProjectsAsync(ws.Id, parentId: null, statusFilter, cancellationToken);
                foreach (var proj in projects)
                {
                    projectDtos.Add(await ToProjectDto(proj, ws.Name, cancellationToken));
                }
            }
        }

        return Ok(new ProjectListResponse { Projects = projectDtos });
    }

    /// <summary>
    /// Get a specific project by ID with memories
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProjectDetailDto>> GetProject(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var projectId = new ProjectId(id);
        var project = await _storage.GetProjectAsync(projectId, cancellationToken);

        if (project == null)
        {
            return NotFound(new { message = $"Project with ID {id} not found" });
        }

        var workspace = await _storage.GetWorkspaceAsync(project.WorkspaceId, cancellationToken);
        var memoryCount = await _storage.GetMemoryCountByOwnerAsync(
            MemoryOwner.ForProject(projectId), cancellationToken);

        // Get recent memories in this project
        var recentMemories = await _storage.GetMemoriesByOwnerAsync(
            MemoryOwner.ForProject(projectId), 1, 10, cancellationToken);

        // Get child projects
        var childProjects = await _storage.GetProjectsAsync(
            project.WorkspaceId,
            parentId: projectId,
            statusFilter: null,
            cancellationToken);

        return Ok(new ProjectDetailDto
        {
            Id = project.Id.Value,
            WorkspaceId = project.WorkspaceId.Value,
            ParentId = project.ParentId?.Value,
            WorkspaceName = workspace?.Name ?? "Unknown",
            Name = project.Name,
            Slug = project.Slug,
            Description = project.Description,
            Status = project.Status.ToString().ToLowerInvariant(),
            VictoryConditions = project.VictoryConditions,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            CompletedAt = project.CompletedAt,
            MemoryCount = memoryCount,
            Memories = recentMemories.Select(m => new MemorySummaryDto
            {
                Id = m.Id.Value,
                Title = m.Title ?? "Untitled",
                Type = m.Type,
                Archetype = m.Archetype.ToStringValue()
            }).ToList(),
            ChildProjects = (await Task.WhenAll(childProjects.Select(async cp =>
                await ToProjectDto(cp, workspace?.Name ?? "Unknown", cancellationToken)))).ToList()
        });
    }

    /// <summary>
    /// Create a new project
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProjectDto>> CreateProject(
        [FromBody] CreateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Name is required" });
        }

        var workspaceId = new WorkspaceId(request.WorkspaceId);
        var workspace = await _storage.GetWorkspaceAsync(workspaceId, cancellationToken);

        if (workspace == null)
        {
            return NotFound(new { message = $"Workspace with ID {request.WorkspaceId} not found" });
        }

        try
        {
            var project = await _storage.CreateProjectAsync(
                workspaceId,
                request.Name,
                request.Description,
                parentId: null,
                cancellationToken);

            // Update victory conditions if provided
            if (!string.IsNullOrWhiteSpace(request.VictoryConditions))
            {
                project = await _storage.UpdateProjectAsync(
                    project.Id,
                    victoryConditions: request.VictoryConditions,
                    cancellationToken: cancellationToken);
            }

            // Set initial status if not draft
            if (!string.IsNullOrWhiteSpace(request.Status) && request.Status.ToLowerInvariant() != "draft")
            {
                var status = ProjectStatusEnumExtensions.ParseProjectStatus(request.Status);
                project = await _storage.UpdateProjectAsync(
                    project.Id,
                    status: status,
                    cancellationToken: cancellationToken);
            }

            return CreatedAtAction(
                nameof(GetProject),
                new { id = project.Id.Value },
                await ToProjectDto(project, workspace.Name, cancellationToken));
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { message = $"A project with the name '{request.Name}' already exists in this workspace" });
        }
    }

    /// <summary>
    /// Update a project
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProjectDto>> UpdateProject(
        Guid id,
        [FromBody] UpdateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectId = new ProjectId(id);
        var existing = await _storage.GetProjectAsync(projectId, cancellationToken);

        if (existing == null)
        {
            return NotFound(new { message = $"Project with ID {id} not found" });
        }

        ProjectStatusEnum? status = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            status = ProjectStatusEnumExtensions.ParseProjectStatus(request.Status);
        }

        try
        {
            var updated = await _storage.UpdateProjectAsync(
                projectId,
                request.Name,
                request.Description,
                status,
                request.VictoryConditions,
                cancellationToken: cancellationToken);

            var workspace = await _storage.GetWorkspaceAsync(updated.WorkspaceId, cancellationToken);
            return Ok(await ToProjectDto(updated, workspace?.Name ?? "Unknown", cancellationToken));
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { message = $"A project with the name '{request.Name}' already exists in this workspace" });
        }
    }

    /// <summary>
    /// Update project status only
    /// </summary>
    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<ProjectDto>> UpdateProjectStatus(
        Guid id,
        [FromBody] UpdateStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectId = new ProjectId(id);
        var existing = await _storage.GetProjectAsync(projectId, cancellationToken);

        if (existing == null)
        {
            return NotFound(new { message = $"Project with ID {id} not found" });
        }

        var status = ProjectStatusEnumExtensions.ParseProjectStatus(request.Status);
        var updated = await _storage.UpdateProjectAsync(
            projectId,
            status: status,
            cancellationToken: cancellationToken);

        var workspace = await _storage.GetWorkspaceAsync(updated.WorkspaceId, cancellationToken);
        return Ok(await ToProjectDto(updated, workspace?.Name ?? "Unknown", cancellationToken));
    }

    /// <summary>
    /// Move project to a different workspace
    /// </summary>
    [HttpPut("{id:guid}/move")]
    public async Task<ActionResult<ProjectDto>> MoveProject(
        Guid id,
        [FromBody] MoveProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectId = new ProjectId(id);
        var existing = await _storage.GetProjectAsync(projectId, cancellationToken);

        if (existing == null)
        {
            return NotFound(new { message = $"Project with ID {id} not found" });
        }

        var newWorkspaceId = new WorkspaceId(request.WorkspaceId);
        var newWorkspace = await _storage.GetWorkspaceAsync(newWorkspaceId, cancellationToken);

        if (newWorkspace == null)
        {
            return NotFound(new { message = $"Workspace with ID {request.WorkspaceId} not found" });
        }

        ProjectId? newParentId = request.NewParentId.HasValue
            ? new ProjectId(request.NewParentId.Value)
            : null;

        try
        {
            var moved = await _storage.MoveProjectToWorkspaceAsync(projectId, newWorkspaceId, newParentId, cancellationToken);
            return Ok(await ToProjectDto(moved, newWorkspace.Name, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { message = "A project with this name already exists in the target workspace. Rename the project before moving." });
        }
    }

    /// <summary>
    /// Delete a project
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteProject(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var projectId = new ProjectId(id);
        var existing = await _storage.GetProjectAsync(projectId, cancellationToken);

        if (existing == null)
        {
            return NotFound(new { message = $"Project with ID {id} not found" });
        }

        // Get memory count for info
        var memoryCount = await _storage.GetMemoryCountByOwnerAsync(
            MemoryOwner.ForProject(projectId), cancellationToken);

        await _storage.DeleteProjectAsync(projectId, cancellationToken);

        return Ok(new {
            message = $"Project '{existing.Name}' deleted",
            memoriesMoved = memoryCount
        });
    }

    private async Task<ProjectDto> ToProjectDto(Project project, string workspaceName, CancellationToken cancellationToken)
    {
        var memoryCount = await _storage.GetMemoryCountByOwnerAsync(
            MemoryOwner.ForProject(project.Id), cancellationToken);

        return new ProjectDto
        {
            Id = project.Id.Value,
            WorkspaceId = project.WorkspaceId.Value,
            ParentId = project.ParentId?.Value,
            WorkspaceName = workspaceName,
            Name = project.Name,
            Slug = project.Slug,
            Description = project.Description,
            Status = project.Status.ToString().ToLowerInvariant(),
            VictoryConditions = project.VictoryConditions,
            MemoryCount = memoryCount,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            CompletedAt = project.CompletedAt
        };
    }
}

// Request/Response DTOs

public class ProjectListResponse
{
    public List<ProjectDto> Projects { get; set; } = new();
}

public class ProjectDto
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ParentId { get; set; }
    public string WorkspaceName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "draft";
    public string? VictoryConditions { get; set; }
    public int MemoryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class ProjectDetailDto : ProjectDto
{
    public List<MemorySummaryDto> Memories { get; set; } = new();
    public List<ProjectDto> ChildProjects { get; set; } = new();
}

public class CreateProjectRequest
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? VictoryConditions { get; set; }
    public string? Status { get; set; }
}

public class UpdateProjectRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
    public string? VictoryConditions { get; set; }
}

public class UpdateStatusRequest
{
    public string Status { get; set; } = "";
}

public class MoveProjectRequest
{
    public Guid WorkspaceId { get; set; }
    public Guid? NewParentId { get; set; }
}
