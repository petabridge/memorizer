using System.ComponentModel;
using System.Text.Json;
using Memorizer.Models;
using Memorizer.Services;
using ModelContextProtocol.Server;

namespace PostgMem.Tools;

/// <summary>
/// MCP resources that expose readable Memorizer context. Where tools perform
/// actions and prompts give guidance, a resource is content a client can read
/// and attach as context.
/// </summary>
[McpServerResourceType]
public class MemorizerResources
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IStorage _storage;

    public MemorizerResources(IStorage storage) => _storage = storage;

    /// <summary>
    /// The live tree of workspaces and their projects. A fixed-URI (non-template)
    /// resource, so it appears in the client's resource list.
    /// </summary>
    [McpServerResource(
        UriTemplate = "memorizer://workspaces",
        Name = "workspace_tree",
        Title = "Workspace and project tree",
        MimeType = "application/json"),
     Description("The current tree of workspaces and the projects inside them. Read this to learn the structure before you store, search, or organize memories.")]
    public async Task<string> WorkspaceTree(CancellationToken cancellationToken)
    {
        var roots = await _storage.GetWorkspacesAsync(parentId: null, includeSystem: false, cancellationToken);

        var nodes = new List<object>();
        foreach (var workspace in roots)
            nodes.Add(await BuildNodeAsync(workspace, cancellationToken));

        return JsonSerializer.Serialize(new { workspaces = nodes }, JsonOptions);
    }

    private async Task<object> BuildNodeAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var projects = await _storage.GetProjectsAsync(workspace.Id, parentId: null, statusFilter: null, cancellationToken);
        var childWorkspaces = await _storage.GetWorkspacesAsync(parentId: workspace.Id, includeSystem: false, cancellationToken);

        var childNodes = new List<object>();
        foreach (var child in childWorkspaces)
            childNodes.Add(await BuildNodeAsync(child, cancellationToken));

        return new
        {
            id = workspace.Id.Value,
            name = workspace.Name,
            slug = workspace.Slug,
            projects = projects.Select(p => new
            {
                id = p.Id.Value,
                name = p.Name,
                status = p.Status.ToString(),
            }).ToList(),
            workspaces = childNodes,
        };
    }
}
