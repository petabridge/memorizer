using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Microsoft.Extensions.Logging;

namespace PostgMem.Tools;

/// <summary>
/// MCP tools for workspace and project management.
/// Provides organization capabilities for memories.
/// </summary>
[McpServerToolType]
public class WorkspaceTools
{
    private readonly IStorage _storage;
    private readonly ILogger<WorkspaceTools> _logger;
    private readonly ICanonicalUrlService _canonicalUrlService;

    public WorkspaceTools(IStorage storage, ILogger<WorkspaceTools> logger, ICanonicalUrlService canonicalUrlService)
    {
        _storage = storage;
        _logger = logger;
        _canonicalUrlService = canonicalUrlService;
    }

    // ===== Workspace Tools =====

    [McpServerTool, Description("Get workspace information. Without an ID, lists root workspaces with hints about nested content. With an ID, shows detailed workspace info. With a query, searches all workspaces by name. Workspaces are organizational containers (e.g., 'Engineering', 'Sales') that persist indefinitely and can be nested.")]
    public async Task<string> GetWorkspace(
        [Description("Optional workspace ID. If omitted, lists root workspaces.")] string? workspaceId = null,
        [Description("Optional search query to find workspaces by name (searches all levels). Case-insensitive partial match.")] string? query = null,
        [Description("Include system workspaces (like 'Unfiled'). Only applies when listing workspaces.")] bool includeSystem = false,
        CancellationToken cancellationToken = default
    )
    {
        // If query is provided, search across all workspaces
        if (!string.IsNullOrWhiteSpace(query))
        {
            return await SearchWorkspacesAsync(query, includeSystem, cancellationToken);
        }

        // Parse optional Guid defensively — MCP clients may send empty strings or "null"
        var parsedWorkspaceId = ParseOptionalGuid(workspaceId);

        // If no ID provided, list root workspaces with hints
        if (!parsedWorkspaceId.HasValue)
        {
            return await ListRootWorkspacesAsync(includeSystem, cancellationToken);
        }

        // Get specific workspace details
        return await GetWorkspaceDetailsAsync(new WorkspaceId(parsedWorkspaceId.Value), cancellationToken);
    }

    private async Task<string> SearchWorkspacesAsync(string query, bool includeSystem, CancellationToken cancellationToken)
    {
        var results = await _storage.SearchWorkspacesAsync(query, includeSystem, cancellationToken);

        if (results.Count == 0)
        {
            return $"No workspaces found matching '{query}'.";
        }

        var result = new StringBuilder();
        result.AppendLine($"Found {results.Count} workspace(s) matching '{query}':");
        result.AppendLine();

        foreach (var item in results)
        {
            var ws = item.Workspace;
            result.AppendLine($"ID: {ws.Id.Value}");
            result.AppendLine($"Name: {ws.Name}");
            result.AppendLine($"Path: {item.FullPath}");
            if (!string.IsNullOrEmpty(ws.Description))
                result.AppendLine($"Description: {ws.Description}");
            result.AppendLine();
        }

        return result.ToString();
    }

    private async Task<string> ListRootWorkspacesAsync(bool includeSystem, CancellationToken cancellationToken)
    {
        var workspaces = await _storage.GetWorkspacesAsync(parentId: null, includeSystem, cancellationToken);

        if (workspaces.Count == 0)
        {
            return "No workspaces found.\n\nHint: Workspaces are persistent domains (e.g., 'Engineering', 'Sales'). Create one with CreateWorkspace.";
        }

        var result = new StringBuilder();
        result.AppendLine($"Found {workspaces.Count} root workspace(s):");
        result.AppendLine();

        foreach (var ws in workspaces)
        {
            // Get child counts for hints
            var childWorkspaces = await _storage.GetWorkspacesAsync(parentId: ws.Id, includeSystem: false, cancellationToken);
            var projects = await _storage.GetProjectsAsync(ws.Id, parentId: null, statusFilter: null, cancellationToken);
            var memoryCount = await _storage.GetMemoryCountByOwnerAsync(MemoryOwner.ForWorkspace(ws.Id), cancellationToken: cancellationToken);

            result.AppendLine($"ID: {ws.Id.Value}");
            result.AppendLine($"Name: {ws.Name}");
            if (!string.IsNullOrEmpty(ws.Description))
                result.AppendLine($"Description: {ws.Description}");
            result.AppendLine($"Memories: {memoryCount}");

            // Hints about what's underneath
            if (childWorkspaces.Count > 0)
            {
                var childNames = string.Join(", ", childWorkspaces.Take(5).Select(c => c.Name));
                if (childWorkspaces.Count > 5)
                    childNames += ", ...";
                result.AppendLine($"Child Workspaces: {childWorkspaces.Count} ({childNames})");
            }
            if (projects.Count > 0)
            {
                var projectNames = string.Join(", ", projects.Take(3).Select(p => p.Name));
                if (projects.Count > 3)
                    projectNames += ", ...";
                result.AppendLine($"Projects: {projects.Count} ({projectNames})");
            }
            result.AppendLine();
        }

        result.AppendLine("Tip: Use GetWorkspace(workspaceId: <id>) to see full details, or GetWorkspace(query: \"name\") to search.");
        return result.ToString();
    }

    private async Task<string> GetWorkspaceDetailsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await _storage.GetWorkspaceAsync(workspaceId, cancellationToken);

        if (workspace == null)
        {
            return $"Workspace with ID {workspaceId.Value} not found.";
        }

        // Get path for breadcrumb
        var path = await _storage.GetWorkspacePathAsync(workspaceId, cancellationToken);
        var fullPath = path.Count == 0
            ? workspace.Name
            : string.Join(" > ", path.Select(p => p.Name).Append(workspace.Name));

        // Get projects in this workspace
        var projects = await _storage.GetProjectsAsync(workspaceId, parentId: null, statusFilter: null, cancellationToken);

        // Get child workspaces
        var childWorkspaces = await _storage.GetWorkspacesAsync(parentId: workspaceId, includeSystem: false, cancellationToken);

        // Get memory count for this workspace
        var workspaceOwner = MemoryOwner.ForWorkspace(workspaceId);
        var memoryCount = await _storage.GetMemoryCountByOwnerAsync(workspaceOwner, cancellationToken: cancellationToken);

        var result = new StringBuilder();
        result.AppendLine($"Workspace: {workspace.Name}");
        result.AppendLine($"ID: {workspace.Id.Value}");
        result.AppendLine($"Path: {fullPath}");
        result.AppendLine($"Slug: {workspace.Slug}");

        if (!string.IsNullOrEmpty(workspace.Description))
            result.AppendLine($"Description: {workspace.Description}");

        // Add canonical URL if configured
        if (_canonicalUrlService.IsConfigured)
        {
            result.AppendLine($"URL: {_canonicalUrlService.GetWorkspaceUrl(workspace.Id)}");
        }

        result.AppendLine();
        result.AppendLine($"Memories: {memoryCount}");
        result.AppendLine($"Projects: {projects.Count}");
        result.AppendLine($"Child Workspaces: {childWorkspaces.Count}");

        if (projects.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("Projects:");
            foreach (var proj in projects.Take(10))
            {
                // Get project memory count for hints
                var projMemCount = await _storage.GetMemoryCountByOwnerAsync(MemoryOwner.ForProject(proj.Id), cancellationToken: cancellationToken);
                result.AppendLine($"  - {proj.Name} ({proj.Status.ToStringValue()}) [{projMemCount} memories] [ID: {proj.Id.Value}]");
            }
            if (projects.Count > 10)
            {
                result.AppendLine($"  ... and {projects.Count - 10} more");
            }
        }

        if (childWorkspaces.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("Child Workspaces:");
            foreach (var child in childWorkspaces.Take(10))
            {
                // Get child workspace memory count for hints
                var childMemCount = await _storage.GetMemoryCountByOwnerAsync(MemoryOwner.ForWorkspace(child.Id), cancellationToken: cancellationToken);
                var childProjects = await _storage.GetProjectsAsync(child.Id, parentId: null, statusFilter: null, cancellationToken);
                var projectHint = childProjects.Count > 0 ? $", {childProjects.Count} projects" : "";
                result.AppendLine($"  - {child.Name} [{childMemCount} memories{projectHint}] [ID: {child.Id.Value}]");
            }
            if (childWorkspaces.Count > 10)
            {
                result.AppendLine($"  ... and {childWorkspaces.Count - 10} more");
            }
        }

        return result.ToString();
    }

    [McpServerTool, Description("Create a new workspace. Workspaces are top-level organizational containers representing major business areas or domains (e.g., 'Engineering', 'Sales', 'Akka.NET'). Workspaces persist indefinitely and contain projects. Memories can belong directly to a workspace OR to a project within a workspace.")]
    public async Task<string> CreateWorkspace(
        [Description("Name for the workspace. Should be descriptive and unique.")] string name,
        [Description("Optional description of the workspace's purpose.")] string? description = null,
        [Description("Optional parent workspace ID to create this as a child/nested workspace. Use GetWorkspace() to find workspace IDs.")] string? parentWorkspaceId = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var parsedParentId = ParseOptionalGuid(parentWorkspaceId);
            var parentId = parsedParentId.HasValue ? new WorkspaceId(parsedParentId.Value) : (WorkspaceId?)null;
            var workspace = await _storage.CreateWorkspaceAsync(name, description, parentId, cancellationToken);

            _logger.LogInformation("Created workspace {WorkspaceId}: {WorkspaceName}", workspace.Id.Value, workspace.Name);

            var urlInfo = _canonicalUrlService.IsConfigured
                ? $"\nURL: {_canonicalUrlService.GetWorkspaceUrl(workspace.Id)}"
                : "";

            return $"Workspace created successfully.\n\nID: {workspace.Id.Value}\nName: {workspace.Name}\nSlug: {workspace.Slug}{urlInfo}\n\nNext steps:\n- Use CreateProject to add completable work items (e.g., 'v2.0 Release', 'Fix Issue #90')\n- Use Store with workspaceId to add general reference memories directly to this workspace\n\nHint: Workspaces are persistent domains. Projects within them are completable and have a lifecycle.";
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate"))
        {
            return $"Failed to create workspace: A workspace with the name '{name}' already exists.";
        }
    }

    [McpServerTool, Description("Update a workspace's properties (name or description). Can also move a workspace under a different parent or promote it to top-level.")]
    public async Task<string> UpdateWorkspace(
        [Description("The workspace ID to update. Use GetWorkspace() to find workspace IDs.")] Guid workspaceId,
        [Description("New name for the workspace. Leave null to keep current.")] string? name = null,
        [Description("New description. Leave null to keep current.")] string? description = null,
        [Description("New parent workspace ID to move this workspace under. Use GetWorkspace() to find IDs. Cannot be used with makeTopLevel.")] Guid? newParentWorkspaceId = null,
        [Description("Set to true to remove the workspace from its current parent and make it a top-level workspace. Cannot be used with newParentWorkspaceId.")] bool makeTopLevel = false,
        CancellationToken cancellationToken = default
    )
    {
        if (newParentWorkspaceId.HasValue && makeTopLevel)
        {
            return "Cannot specify both newParentWorkspaceId and makeTopLevel. Use one or the other.";
        }

        var typedWorkspaceId = new WorkspaceId(workspaceId);

        // Verify workspace exists
        var existing = await _storage.GetWorkspaceAsync(typedWorkspaceId, cancellationToken);
        if (existing == null)
        {
            return $"Workspace with ID {workspaceId} not found.";
        }

        if (existing.IsSystem)
        {
            return "Cannot modify system workspaces.";
        }

        WorkspaceId? typedNewParentId = newParentWorkspaceId.HasValue
            ? new WorkspaceId(newParentWorkspaceId.Value)
            : null;

        try
        {
            var workspace = await _storage.UpdateWorkspaceAsync(
                typedWorkspaceId,
                name,
                description,
                typedNewParentId,
                makeTopLevel,
                cancellationToken
            );

            var changes = new List<string>();
            if (name != null) changes.Add($"name='{name}'");
            if (description != null) changes.Add("description updated");
            if (newParentWorkspaceId.HasValue) changes.Add($"moved under workspace {newParentWorkspaceId.Value}");
            if (makeTopLevel) changes.Add("promoted to top-level");

            _logger.LogInformation("Updated workspace {WorkspaceId}: {Changes}", workspaceId, string.Join(", ", changes));

            return $"Workspace updated successfully. Changes: {string.Join(", ", changes)}";
        }
        catch (InvalidOperationException ex)
        {
            return $"Failed to update workspace: {ex.Message}";
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate"))
        {
            return $"Failed to update workspace: A workspace with that name already exists at the target level. Rename before moving.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update workspace {WorkspaceId}", workspaceId);
            return $"Failed to update workspace: {ex.Message}";
        }
    }

    [McpServerTool, Description("Delete a workspace. All memories in the workspace will be moved to Unfiled. Projects must be deleted separately first.")]
    public async Task<string> DeleteWorkspace(
        [Description("The workspace ID to delete. Use ListWorkspaces to find workspace IDs.")] Guid workspaceId,
        CancellationToken cancellationToken = default
    )
    {
        var typedWorkspaceId = new WorkspaceId(workspaceId);

        // Verify workspace exists
        var workspace = await _storage.GetWorkspaceAsync(typedWorkspaceId, cancellationToken);
        if (workspace == null)
        {
            return $"Workspace with ID {workspaceId} not found.";
        }

        if (workspace.IsSystem)
        {
            return "Cannot delete system workspaces.";
        }

        // Check for projects
        var projects = await _storage.GetProjectsAsync(typedWorkspaceId, parentId: null, statusFilter: null, cancellationToken);
        if (projects.Count > 0)
        {
            return $"Cannot delete workspace '{workspace.Name}': it contains {projects.Count} project(s). Delete the projects first using DeleteProject.";
        }

        // Check for child workspaces
        var childWorkspaces = await _storage.GetWorkspacesAsync(parentId: typedWorkspaceId, includeSystem: false, cancellationToken);
        if (childWorkspaces.Count > 0)
        {
            return $"Cannot delete workspace '{workspace.Name}': it contains {childWorkspaces.Count} child workspace(s). Delete the child workspaces first.";
        }

        // Get memory count for context
        var workspaceOwner = MemoryOwner.ForWorkspace(typedWorkspaceId);
        var memoryCount = await _storage.GetMemoryCountByOwnerAsync(workspaceOwner, cancellationToken: cancellationToken);

        try
        {
            await _storage.DeleteWorkspaceAsync(typedWorkspaceId, cancellationToken);

            _logger.LogInformation("Deleted workspace {WorkspaceId}: {WorkspaceName}", workspaceId, workspace.Name);

            var message = $"Workspace '{workspace.Name}' deleted successfully.";
            if (memoryCount > 0)
            {
                message += $"\n{memoryCount} memories were moved to Unfiled.";
            }
            return message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete workspace {WorkspaceId}", workspaceId);
            return $"Failed to delete workspace: {ex.Message}";
        }
    }

    // ===== Project Tools =====

    [McpServerTool, Description("Get project information. With a project ID, shows detailed project info. With a workspace ID (no project ID), lists projects in that workspace. With a query, searches all projects by name. Projects are goal-oriented, completable work items with a lifecycle.")]
    public async Task<string> GetProjectContext(
        [Description("Optional project ID. If provided, shows detailed project info.")] string? projectId = null,
        [Description("Optional workspace ID. If provided without projectId, lists projects in that workspace.")] string? workspaceId = null,
        [Description("Optional search query to find projects by name (searches all workspaces). Case-insensitive partial match.")] string? query = null,
        [Description("Filter by status: 'active' (default), 'completed', 'archived', 'all'. Applies to listing and searching.")] string status = "active",
        [Description("Include memory list with IDs, titles, and archetypes in project details. Default: true.")] bool includeMemories = true,
        CancellationToken cancellationToken = default
    )
    {
        // Parse status filter
        ProjectStatusEnum? statusFilter = status.ToLowerInvariant() switch
        {
            "all" => null,
            "active" => ProjectStatusEnum.Active,
            "draft" => ProjectStatusEnum.Draft,
            "completed" => ProjectStatusEnum.Completed,
            "archived" => ProjectStatusEnum.Archived,
            "cancelled" or "canceled" => ProjectStatusEnum.Cancelled,
            "onhold" or "on_hold" or "on-hold" => ProjectStatusEnum.OnHold,
            _ => ProjectStatusEnum.Active
        };

        // If query is provided, search across all projects
        if (!string.IsNullOrWhiteSpace(query))
        {
            return await SearchProjectsAsync(query, statusFilter, cancellationToken);
        }

        // Parse optional Guid parameters defensively — MCP clients may send empty strings or "null"
        var parsedProjectId = ParseOptionalGuid(projectId);
        var parsedWorkspaceId = ParseOptionalGuid(workspaceId);

        // If project ID provided, get detailed project info
        if (parsedProjectId.HasValue)
        {
            return await GetProjectDetailsAsync(new ProjectId(parsedProjectId.Value), includeMemories, cancellationToken);
        }

        // If workspace ID provided, list projects in that workspace
        if (parsedWorkspaceId.HasValue)
        {
            return await ListProjectsInWorkspaceAsync(new WorkspaceId(parsedWorkspaceId.Value), statusFilter, cancellationToken);
        }

        return "Please provide either a projectId, workspaceId, or query parameter.\n\nExamples:\n- GetProjectContext(projectId: <id>) - Get project details\n- GetProjectContext(workspaceId: <id>) - List projects in workspace\n- GetProjectContext(query: \"billing\") - Search all projects by name";
    }

    private async Task<string> SearchProjectsAsync(string query, ProjectStatusEnum? statusFilter, CancellationToken cancellationToken)
    {
        var results = await _storage.SearchProjectsAsync(query, statusFilter, cancellationToken);

        if (results.Count == 0)
        {
            return $"No projects found matching '{query}'.";
        }

        var result = new StringBuilder();
        result.AppendLine($"Found {results.Count} project(s) matching '{query}':");
        result.AppendLine();

        foreach (var item in results)
        {
            var proj = item.Project;
            result.AppendLine($"ID: {proj.Id.Value}");
            result.AppendLine($"Name: {proj.Name}");
            result.AppendLine($"Path: {item.FullPath}");
            result.AppendLine($"Status: {proj.Status.ToStringValue()}");
            if (!string.IsNullOrEmpty(proj.Description))
                result.AppendLine($"Description: {proj.Description}");
            result.AppendLine();
        }

        return result.ToString();
    }

    private async Task<string> ListProjectsInWorkspaceAsync(WorkspaceId workspaceId, ProjectStatusEnum? statusFilter, CancellationToken cancellationToken)
    {
        var workspace = await _storage.GetWorkspaceAsync(workspaceId, cancellationToken);
        if (workspace == null)
        {
            return $"Workspace with ID {workspaceId.Value} not found.";
        }

        var projects = await _storage.GetProjectsAsync(workspaceId, parentId: null, statusFilter, cancellationToken);

        if (projects.Count == 0)
        {
            var statusHint = statusFilter.HasValue ? $" with status '{statusFilter.Value.ToStringValue()}'" : "";
            return $"No projects found{statusHint} in workspace '{workspace.Name}'.\n\nHint: Use CreateProject to add completable work items.";
        }

        var result = new StringBuilder();
        result.AppendLine($"Projects in '{workspace.Name}':");
        result.AppendLine();

        foreach (var proj in projects)
        {
            // Get memory count and subproject count for hints
            var memCount = await _storage.GetMemoryCountByOwnerAsync(MemoryOwner.ForProject(proj.Id), cancellationToken: cancellationToken);
            var subprojects = await _storage.GetProjectsAsync(workspaceId, parentId: proj.Id, statusFilter: null, cancellationToken);

            result.AppendLine($"ID: {proj.Id.Value}");
            result.AppendLine($"Name: {proj.Name}");
            result.AppendLine($"Status: {proj.Status.ToStringValue()}");
            result.AppendLine($"Memories: {memCount}");
            if (subprojects.Count > 0)
            {
                var subNames = string.Join(", ", subprojects.Take(3).Select(s => s.Name));
                if (subprojects.Count > 3) subNames += ", ...";
                result.AppendLine($"Subprojects: {subprojects.Count} ({subNames})");
            }
            if (!string.IsNullOrEmpty(proj.Description))
                result.AppendLine($"Description: {proj.Description}");
            result.AppendLine();
        }

        result.AppendLine("Tip: Use GetProjectContext(projectId: <id>) for full details including victory conditions.");
        return result.ToString();
    }

    private async Task<string> GetProjectDetailsAsync(ProjectId projectId, bool includeMemories, CancellationToken cancellationToken)
    {
        var project = await _storage.GetProjectAsync(projectId, cancellationToken);

        if (project == null)
        {
            return $"Project with ID {projectId.Value} not found.";
        }

        // Get path for breadcrumb
        var path = await _storage.GetProjectPathAsync(projectId, cancellationToken);
        var fullPath = path.GetFullPath(project.Name);

        // Get child projects
        var childProjects = await _storage.GetProjectsAsync(
            project.WorkspaceId,
            parentId: projectId,
            statusFilter: null,
            cancellationToken
        );

        // Get memories for this project (either full list or just count)
        var memoryOwner = MemoryOwner.ForProject(projectId);
        IReadOnlyList<Memorizer.Models.Memory>? memories = null;
        int memoryCount;

        if (includeMemories)
        {
            // Fetch actual memories (limit to reasonable amount for display)
            memories = await _storage.GetMemoriesByOwnerAsync(memoryOwner, page: 1, pageSize: 100, cancellationToken: cancellationToken);
            memoryCount = memories.Count;
        }
        else
        {
            memoryCount = await _storage.GetMemoryCountByOwnerAsync(memoryOwner, cancellationToken: cancellationToken);
        }

        var result = new StringBuilder();
        result.AppendLine($"Project: {project.Name}");
        result.AppendLine($"ID: {project.Id.Value}");
        result.AppendLine($"Path: {fullPath}");
        result.AppendLine($"Status: {project.Status.ToStringValue()}");

        if (!string.IsNullOrEmpty(project.Description))
            result.AppendLine($"Description: {project.Description}");

        // Add canonical URL if configured
        if (_canonicalUrlService.IsConfigured)
        {
            result.AppendLine($"URL: {_canonicalUrlService.GetProjectUrl(project.Id)}");
        }

        if (!string.IsNullOrEmpty(project.VictoryConditions))
        {
            result.AppendLine();
            result.AppendLine("Victory Conditions:");
            result.AppendLine(project.VictoryConditions);
        }

        result.AppendLine();
        result.AppendLine($"Memories: {memoryCount}");

        // List memories with IDs, titles, and archetypes
        if (includeMemories && memories != null && memories.Count > 0)
        {
            foreach (var memory in memories)
            {
                var title = memory.Title ?? "Untitled";
                var archetype = memory.Archetype.ToStringValue();
                result.AppendLine($"  - {memory.Id.Value}: {title} ({archetype})");
            }
        }

        if (childProjects.Count > 0)
        {
            result.AppendLine();
            result.AppendLine($"Subprojects ({childProjects.Count}):");
            foreach (var child in childProjects)
            {
                var childMemCount = await _storage.GetMemoryCountByOwnerAsync(MemoryOwner.ForProject(child.Id), cancellationToken: cancellationToken);
                result.AppendLine($"  - {child.Name} ({child.Status.ToStringValue()}) [{childMemCount} memories]");
                result.AppendLine($"    ID: {child.Id.Value}");
            }
        }

        result.AppendLine();
        result.AppendLine($"Created: {project.CreatedAt:yyyy-MM-dd HH:mm}");
        result.AppendLine($"Updated: {project.UpdatedAt:yyyy-MM-dd HH:mm}");

        return result.ToString();
    }

    [McpServerTool, Description("Create a new project within a workspace. Projects are goal-oriented, completable units of work (e.g., 'Implement Feature X', 'Fix Bug Y', 'Q4 Marketing Campaign'). Projects have a lifecycle (draft → active → completed/archived) and optional victory conditions to define success criteria.")]
    public async Task<string> CreateProject(
        [Description("The workspace ID to create the project in. Use GetWorkspace() to find workspace IDs.")] Guid workspaceId,
        [Description("Name for the project. Should be descriptive.")] string name,
        [Description("Optional description of the project's purpose and scope.")] string? description = null,
        [Description("Optional victory conditions - what success looks like for this project.")] string? victoryConditions = null,
        [Description("Optional parent project ID to create this as a subproject. Use GetProjectContext() to find project IDs.")] string? parentProjectId = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var typedWorkspaceId = new WorkspaceId(workspaceId);
            var parsedParentId = ParseOptionalGuid(parentProjectId);
            var parentId = parsedParentId.HasValue ? new ProjectId(parsedParentId.Value) : (ProjectId?)null;
            var project = await _storage.CreateProjectAsync(typedWorkspaceId, name, description, parentId, cancellationToken);

            // If victory conditions provided, update the project
            if (!string.IsNullOrWhiteSpace(victoryConditions))
            {
                project = await _storage.UpdateProjectAsync(project.Id, victoryConditions: victoryConditions, cancellationToken: cancellationToken);
            }

            _logger.LogInformation("Created project {ProjectId}: {ProjectName} in workspace {WorkspaceId}",
                project.Id.Value, project.Name, workspaceId);

            var result = new StringBuilder();
            result.AppendLine("Project created successfully.");
            result.AppendLine();
            result.AppendLine($"ID: {project.Id.Value}");
            result.AppendLine($"Name: {project.Name}");
            result.AppendLine($"Status: {project.Status.ToStringValue()}");
            if (!string.IsNullOrEmpty(project.Description))
                result.AppendLine($"Description: {project.Description}");
            if (!string.IsNullOrEmpty(project.VictoryConditions))
                result.AppendLine($"Victory Conditions: {project.VictoryConditions}");

            // Add canonical URL if configured
            if (_canonicalUrlService.IsConfigured)
            {
                result.AppendLine($"URL: {_canonicalUrlService.GetProjectUrl(project.Id)}");
            }

            result.AppendLine();
            result.AppendLine("Next steps:");
            result.AppendLine("- Use Store with projectId to add memories specific to this work");
            result.AppendLine("- Use MoveMemory to organize existing memories into this project");
            result.AppendLine("- Use UpdateProject to mark as 'completed' when victory conditions are met");

            return result.ToString();
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate"))
        {
            return $"Failed to create project: A project with the name '{name}' already exists in this workspace.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create project {ProjectName} in workspace {WorkspaceId}", name, workspaceId);
            return $"Failed to create project: {ex.Message}";
        }
    }

    [McpServerTool, Description("Update a project's properties (name, description, status, victory conditions, or parent). Can also move a project under a different parent or make it top-level.")]
    public async Task<string> UpdateProject(
        [Description("The project ID to update. Use GetProjectContext() to find project IDs.")] Guid projectId,
        [Description("New name for the project. Leave null to keep current.")] string? name = null,
        [Description("New description. Leave null to keep current.")] string? description = null,
        [Description("New status: 'draft', 'active', 'on_hold', 'completed', 'cancelled', or 'archived'.")] string? status = null,
        [Description("New victory conditions. Leave null to keep current.")] string? victoryConditions = null,
        [Description("New parent project ID to move this project under. The parent must be in the same workspace. Cannot be used with makeTopLevel.")] string? parentProjectId = null,
        [Description("Set to true to remove the project from its current parent and make it a top-level project in its workspace. Cannot be used with parentProjectId.")] bool makeTopLevel = false,
        [Description("Move this project (and all subprojects) to a different workspace. Provide the target workspace ID. Use GetWorkspace() to find workspace IDs.")] Guid? newWorkspaceId = null,
        CancellationToken cancellationToken = default
    )
    {
        var typedProjectId = new ProjectId(projectId);

        // Parse optional Guid defensively — MCP clients may send empty strings or "null"
        var parsedParentProjectId = ParseOptionalGuid(parentProjectId);

        // If moving to a different workspace, route to MoveProjectToWorkspaceAsync
        if (newWorkspaceId.HasValue)
        {
            var existing = await _storage.GetProjectAsync(typedProjectId, cancellationToken);
            if (existing == null)
                return $"Project with ID {projectId} not found.";

            if (existing.WorkspaceId.Value == newWorkspaceId.Value)
                return "Project is already in the specified workspace.";

            ProjectId? typedNewParentIdForMove = parsedParentProjectId.HasValue ? new ProjectId(parsedParentProjectId.Value) : null;

            try
            {
                var moved = await _storage.MoveProjectToWorkspaceAsync(
                    typedProjectId,
                    new WorkspaceId(newWorkspaceId.Value),
                    typedNewParentIdForMove,
                    cancellationToken);

                _logger.LogInformation("Moved project {ProjectId} to workspace {WorkspaceId}", projectId, newWorkspaceId.Value);
                return $"Project moved successfully to workspace {newWorkspaceId.Value}. Project ID: {moved.Id.Value}";
            }
            catch (InvalidOperationException ex)
            {
                return $"Failed to move project: {ex.Message}";
            }
            catch (Exception ex) when (ex.Message.Contains("duplicate"))
            {
                return $"Failed to move project: A project with this name already exists in the target workspace. Rename before moving.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to move project {ProjectId}", projectId);
                return $"Failed to move project: {ex.Message}";
            }
        }

        // Validate same-workspace move parameters
        if (parsedParentProjectId.HasValue && makeTopLevel)
        {
            return "Cannot specify both parentProjectId and makeTopLevel. Use one or the other to move the project.";
        }

        // Parse status if provided
        ProjectStatusEnum? statusEnum = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            statusEnum = ProjectStatusEnumExtensions.ParseProjectStatus(status);
        }

        // Convert parent project ID to typed ID
        ProjectId? typedNewParentId = parsedParentProjectId.HasValue ? new ProjectId(parsedParentProjectId.Value) : null;

        try
        {
            var project = await _storage.UpdateProjectAsync(
                typedProjectId,
                name,
                description,
                statusEnum,
                victoryConditions,
                typedNewParentId,
                makeTopLevel,
                cancellationToken
            );

            var changes = new List<string>();
            if (name != null) changes.Add($"name='{name}'");
            if (description != null) changes.Add("description updated");
            if (statusEnum != null) changes.Add($"status='{statusEnum.Value.ToStringValue()}'");
            if (victoryConditions != null) changes.Add("victory conditions updated");
            if (parsedParentProjectId.HasValue) changes.Add($"moved under project {parsedParentProjectId.Value}");
            if (makeTopLevel) changes.Add("moved to top-level (removed from parent)");

            _logger.LogInformation("Updated project {ProjectId}: {Changes}", projectId, string.Join(", ", changes));

            return $"Project updated successfully. Changes: {string.Join(", ", changes)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update project {ProjectId}", projectId);
            return $"Failed to update project: {ex.Message}";
        }
    }

    [McpServerTool, Description("Delete a project. All memories in the project will be moved to Unfiled.")]
    public async Task<string> DeleteProject(
        [Description("The project ID to delete. Use ListProjects to find project IDs.")] Guid projectId,
        CancellationToken cancellationToken = default
    )
    {
        var typedProjectId = new ProjectId(projectId);

        // Verify project exists
        var project = await _storage.GetProjectAsync(typedProjectId, cancellationToken);
        if (project == null)
        {
            return $"Project with ID {projectId} not found.";
        }

        // Check for child projects before deletion
        var childProjects = await _storage.GetProjectsAsync(
            project.WorkspaceId,
            parentId: typedProjectId,
            statusFilter: null,
            cancellationToken
        );

        if (childProjects.Count > 0)
        {
            return $"Cannot delete project '{project.Name}': it contains {childProjects.Count} child project(s). Delete the children first or they will be deleted automatically due to CASCADE constraint.";
        }

        // Get memory count for context
        var projectOwner = MemoryOwner.ForProject(typedProjectId);
        var memoryCount = await _storage.GetMemoryCountByOwnerAsync(projectOwner, cancellationToken: cancellationToken);

        try
        {
            await _storage.DeleteProjectAsync(typedProjectId, cancellationToken);

            _logger.LogInformation("Deleted project {ProjectId}: {ProjectName}", projectId, project.Name);

            var message = $"Project '{project.Name}' deleted successfully.";
            if (memoryCount > 0)
            {
                message += $"\n{memoryCount} memories were moved to Unfiled.";
            }
            return message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project {ProjectId}", projectId);
            return $"Failed to delete project: {ex.Message}";
        }
    }

    // ===== Memory Organization Tools =====

    [McpServerTool, Description("Move one or more memories to a project, workspace, or Unfiled. Consolidates all memory organization operations into a single tool.")]
    public async Task<string> MoveMemory(
        [Description("Memory ID(s) to move. Can be a single ID or array of IDs. Use Search or Get to find memory IDs.")] Guid[] memoryIds,
        [Description("Destination project ID. Use this to move memories into a project. Use ListProjects to find project IDs.")] string? projectId = null,
        [Description("Destination workspace ID. Use this to move memories directly to a workspace (not a project). Use ListWorkspaces to find workspace IDs.")] string? workspaceId = null,
        [Description("Set to true to move memories to the Unfiled workspace (unassign from current project/workspace).")] bool toUnfiled = false,
        CancellationToken cancellationToken = default
    )
    {
        // Validate inputs
        if (memoryIds == null || memoryIds.Length == 0)
        {
            return "No memory IDs provided.";
        }

        // Parse optional Guid parameters defensively — MCP clients may send empty strings or "null"
        var parsedProjectId = ParseOptionalGuid(projectId);
        var parsedWorkspaceId = ParseOptionalGuid(workspaceId);

        var destinationCount = (parsedProjectId.HasValue ? 1 : 0) + (parsedWorkspaceId.HasValue ? 1 : 0) + (toUnfiled ? 1 : 0);
        if (destinationCount == 0)
        {
            return "Must specify a destination: projectId, workspaceId, or toUnfiled=true.";
        }
        if (destinationCount > 1)
        {
            return "Specify only one destination: projectId, workspaceId, or toUnfiled.";
        }

        // Determine destination and validate it exists
        MemoryOwner owner = default;
        string destinationName;

        if (parsedProjectId.HasValue)
        {
            var typedProjectId = new ProjectId(parsedProjectId.Value);
            var project = await _storage.GetProjectAsync(typedProjectId, cancellationToken);
            if (project == null)
            {
                return $"Project with ID {parsedProjectId} not found.";
            }
            owner = MemoryOwner.ForProject(typedProjectId);
            destinationName = $"project '{project.Name}'";
        }
        else if (parsedWorkspaceId.HasValue)
        {
            var typedWorkspaceId = new WorkspaceId(parsedWorkspaceId.Value);
            var workspace = await _storage.GetWorkspaceAsync(typedWorkspaceId, cancellationToken);
            if (workspace == null)
            {
                return $"Workspace with ID {parsedWorkspaceId} not found.";
            }
            owner = MemoryOwner.ForWorkspace(typedWorkspaceId);
            destinationName = $"workspace '{workspace.Name}'";
        }
        else // toUnfiled
        {
            destinationName = "Unfiled workspace";
        }

        // Move the memories
        var movedCount = 0;
        var notFoundIds = new List<Guid>();

        foreach (var memoryId in memoryIds)
        {
            var typedMemoryId = new MemoryId(memoryId);

            // Verify the memory exists
            var memory = await _storage.Get(typedMemoryId, cancellationToken);
            if (memory == null)
            {
                notFoundIds.Add(memoryId);
                continue;
            }

            if (toUnfiled)
            {
                await _storage.MoveMemoryToUnfiledAsync(typedMemoryId, cancellationToken);
            }
            else
            {
                await _storage.SetMemoryOwnerAsync(typedMemoryId, owner, cancellationToken);
            }
            movedCount++;
        }

        _logger.LogInformation("Moved {Count} memories to {Destination}", movedCount, destinationName);

        // Build result message
        string organizationHint = parsedProjectId.HasValue
            ? "\n\nHint: Memories in projects are scoped to that completable work. When the project is done, memories remain accessible."
            : parsedWorkspaceId.HasValue
                ? "\n\nHint: Memories directly in a workspace are general reference for that domain. Use projects for work-specific memories."
                : "\n\nHint: Unfiled memories can be organized later into workspaces (persistent domains) or projects (completable work).";

        if (memoryIds.Length == 1)
        {
            if (notFoundIds.Count > 0)
            {
                return $"Memory with ID {memoryIds[0]} not found.";
            }
            return $"Memory successfully moved to {destinationName}.{organizationHint}";
        }

        var result = new StringBuilder();
        result.AppendLine($"Moved {movedCount} of {memoryIds.Length} memories to {destinationName}.");

        if (notFoundIds.Count > 0)
        {
            result.AppendLine();
            result.AppendLine($"Not found ({notFoundIds.Count}): {string.Join(", ", notFoundIds)}");
        }

        return result.ToString();
    }

    /// <summary>
    /// Parses an optional Guid parameter defensively. MCP clients (e.g. Cursor, Ollama-based clients)
    /// may send empty strings or the literal string "null" instead of a proper JSON null for optional
    /// Guid fields. This helper treats those as null rather than throwing a deserialization exception.
    /// </summary>
    private static Guid? ParseOptionalGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        return Guid.TryParse(value, out var guid) ? guid : null;
    }
}
