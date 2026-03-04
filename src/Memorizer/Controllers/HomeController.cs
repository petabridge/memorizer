using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.AspNetCore.Mvc;

namespace Memorizer.Controllers;

[Route("")]
public class HomeController : Controller
{
    private readonly IMemoryStatsService _statsService;
    private readonly IStorage _storage;
    private readonly ServerSettings _serverSettings;

    public HomeController(IMemoryStatsService statsService, IStorage storage, ServerSettings serverSettings)
    {
        _statsService = statsService;
        _storage = storage;
        _serverSettings = serverSettings;
    }

    /// <summary>
    /// Default landing page - shows workspaces
    /// </summary>
    [HttpGet]
    [Route("")]
    public IActionResult Index()
    {
        return View("Workspaces");
    }

    /// <summary>
    /// Memory list page - browse all memories with search/filter
    /// </summary>
    [HttpGet]
    [Route("memories")]
    public IActionResult Memories()
    {
        return View("Index"); // Reuse the existing Index.cshtml view
    }

    /// <summary>
    /// Memory details/edit page
    /// </summary>
    [HttpGet]
    [Route("edit/{id:guid}")]
    public IActionResult Edit(Guid id)
    {
        ViewBag.MemoryId = id;
        return View();
    }

    /// <summary>
    /// Memory view page - read-only with rendered markdown
    /// </summary>
    [HttpGet]
    [Route("view/{id:guid}")]
    public IActionResult View(Guid id)
    {
        ViewBag.MemoryId = id;
        return View();
    }

    /// <summary>
    /// Create new memory page
    /// </summary>
    [HttpGet]
    [Route("create")]
    public IActionResult Create()
    {
        return View();
    }

    /// <summary>
    /// Enhanced stats dashboard page
    /// </summary>
    [HttpGet]
    [Route("stats")]
    public async Task<IActionResult> Stats()
    {
        var stats = await _statsService.GetStatsAsync();
        return View(stats);
    }

    /// <summary>
    /// MCP configuration page - shows configuration UI
    /// </summary>
    [HttpGet]
    [Route("mcp-config")]
    public IActionResult McpConfig()
    {
        return View();
    }

    /// <summary>
    /// System configuration page - shows all settings for debugging
    /// </summary>
    [HttpGet]
    [Route("config")]
    public IActionResult Config()
    {
        return View();
    }

    /// <summary>
    /// Workspaces list page - manage all workspaces
    /// </summary>
    [HttpGet]
    [Route("workspaces")]
    public IActionResult Workspaces()
    {
        return View();
    }

    /// <summary>
    /// Workspace detail page - view and edit a workspace
    /// </summary>
    [HttpGet]
    [Route("workspaces/{id:guid}")]
    public IActionResult WorkspaceDetail(Guid id)
    {
        ViewBag.WorkspaceId = id;
        return View();
    }

    /// <summary>
    /// Project detail page - view and edit a project
    /// </summary>
    [HttpGet]
    [Route("projects/{id:guid}")]
    public IActionResult ProjectDetail(Guid id)
    {
        ViewBag.ProjectId = id;
        return View();
    }

    /// <summary>
    /// View a specific version of a memory
    /// </summary>
    [HttpGet]
    [Route("view/{id:guid}/version/{versionNumber:int}")]
    public IActionResult ViewVersion(Guid id, int versionNumber)
    {
        ViewBag.MemoryId = id;
        ViewBag.VersionNumber = versionNumber;
        return View();
    }

    /// <summary>
    /// Compare two versions of a memory
    /// </summary>
    [HttpGet]
    [Route("view/{id:guid}/compare/{fromVersion:int}/{toVersion:int}")]
    public IActionResult CompareVersions(Guid id, int fromVersion, int toVersion)
    {
        ViewBag.MemoryId = id;
        ViewBag.FromVersion = fromVersion;
        ViewBag.ToVersion = toVersion;
        return View();
    }

    /// <summary>
    /// MCP configuration JSON endpoint - returns simplified JSON configuration for MCP client
    /// </summary>
    [HttpGet]
    [Route("mcp-config-json")]
    public IActionResult McpConfigJson()
    {
        var canonicalUrl = _serverSettings?.CanonicalUrl ?? "http://localhost:5000";
        // MCP endpoint is at /mcp path
        var mcpUrl = canonicalUrl.TrimEnd('/') + "/mcp";

        var mcpConfig = new
        {
            mcpServers = new
            {
                memorizer = new
                {
                    url = mcpUrl
                }
            }
        };

        return Json(mcpConfig);
    }

    /// <summary>
    /// API endpoint returning workspace/project hierarchy for sidebar tree navigation.
    /// </summary>
    [HttpGet]
    [Route("api/workspace-tree")]
    public async Task<IActionResult> GetWorkspaceTree(CancellationToken cancellationToken)
    {
        // Get root workspaces (excluding system workspaces like Unfiled)
        var rootWorkspaces = await _storage.GetWorkspacesAsync(parentId: null, includeSystem: false, cancellationToken);

        // Build tree structure with projects and memory counts
        var workspaceNodes = new List<object>();
        foreach (var workspace in rootWorkspaces)
        {
            var node = await BuildWorkspaceNodeAsync(workspace, cancellationToken);
            workspaceNodes.Add(node);
        }

        // Get unfiled memory count
        var unfiledCount = await _storage.GetUnfiledMemoryCountAsync(cancellationToken: cancellationToken);

        var result = new
        {
            workspaces = workspaceNodes,
            unfiledCount
        };

        return Json(result);
    }

    private async Task<object> BuildWorkspaceNodeAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        // Get memory count directly in this workspace (not in projects)
        var directMemoryCount = await _storage.GetMemoryCountByOwnerAsync(
            MemoryOwner.ForWorkspace(workspace.Id), cancellationToken: cancellationToken);

        // Get projects in this workspace and build nodes, also aggregate project memory counts
        var projects = await _storage.GetProjectsAsync(workspace.Id, parentId: null, statusFilter: null, cancellationToken);
        var projectNodes = new List<object>();
        var totalProjectMemories = 0;
        foreach (var project in projects)
        {
            var (projectNode, projectMemoryCount) = await BuildProjectNodeWithCountAsync(project, cancellationToken);
            projectNodes.Add(projectNode);
            totalProjectMemories += projectMemoryCount;
        }

        // Get child workspaces (nested) - not aggregating their counts into parent for now
        var childWorkspaces = await _storage.GetWorkspacesAsync(parentId: workspace.Id, includeSystem: false, cancellationToken);
        var childNodes = new List<object>();
        foreach (var child in childWorkspaces)
        {
            var childNode = await BuildWorkspaceNodeAsync(child, cancellationToken);
            childNodes.Add(childNode);
        }

        // Total includes direct workspace memories + all project memories
        var totalMemoryCount = directMemoryCount + totalProjectMemories;

        return new
        {
            id = workspace.Id.Value,
            name = workspace.Name,
            slug = workspace.Slug,
            description = workspace.Description,
            memoryCount = totalMemoryCount,
            projects = projectNodes,
            children = childNodes
        };
    }

    private async Task<object> BuildProjectNodeAsync(Project project, CancellationToken cancellationToken)
    {
        var (node, _) = await BuildProjectNodeWithCountAsync(project, cancellationToken);
        return node;
    }

    private async Task<(object Node, int MemoryCount)> BuildProjectNodeWithCountAsync(Project project, CancellationToken cancellationToken)
    {
        // Get memory count for this project
        var memoryCount = await _storage.GetMemoryCountByOwnerAsync(
            MemoryOwner.ForProject(project.Id), cancellationToken: cancellationToken);

        // Get child projects (nested)
        var childProjects = await _storage.GetProjectsAsync(project.WorkspaceId, parentId: project.Id, statusFilter: null, cancellationToken);
        var childNodes = new List<object>();
        var childMemoryCount = 0;
        foreach (var child in childProjects)
        {
            var (childNode, childCount) = await BuildProjectNodeWithCountAsync(child, cancellationToken);
            childNodes.Add(childNode);
            childMemoryCount += childCount;
        }

        var node = new
        {
            id = project.Id.Value,
            workspaceId = project.WorkspaceId.Value,
            name = project.Name,
            slug = project.Slug,
            description = project.Description,
            status = project.Status.ToString().ToLowerInvariant(),
            memoryCount,
            children = childNodes
        };

        // Return node and total count (this project + all nested child projects)
        return (node, memoryCount + childMemoryCount);
    }
} 