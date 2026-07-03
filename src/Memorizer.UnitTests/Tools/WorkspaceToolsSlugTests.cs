using Memorizer.Models;
using Memorizer.Models.ValueTypes;
using Microsoft.Extensions.Logging.Abstractions;
using PostgMem.Tools;

namespace Memorizer.UnitTests.Tools;

/// <summary>
/// Tests that verify the GetWorkspace MCP tool resolves workspaces by slug.
/// Uses manual fakes instead of a mocking framework.
/// </summary>
public class WorkspaceToolsSlugTests
{
    [Fact]
    public async Task GetWorkspace_BySlug_ShouldReturnWorkspaceDetails()
    {
        // Arrange
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            WorkspaceBySlug = CreateTestWorkspace(workspaceId, "Engineering")
        };
        var tools = CreateTools(fakeStorage);

        // Act
        var result = await tools.GetWorkspace(slug: "engineering");

        // Assert
        Assert.Contains("Workspace: Engineering", result);
        Assert.Contains("Slug: engineering", result);
        Assert.Equal("engineering", fakeStorage.LastSlugQueried);
        Assert.Null(fakeStorage.LastParentIdQueried);
    }

    [Fact]
    public async Task GetWorkspace_BySlug_WithParent_ShouldScopeLookupToParent()
    {
        // Arrange
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var parentId = new WorkspaceId(Guid.Parse("a1874a6b-8a15-4da6-a413-99bf3249d1e4"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            WorkspaceBySlug = CreateTestWorkspace(workspaceId, "Backend")
        };
        var tools = CreateTools(fakeStorage);

        // Act
        var result = await tools.GetWorkspace(slug: "backend", parentWorkspaceId: parentId.Value.ToString());

        // Assert
        Assert.Contains("Workspace: Backend", result);
        Assert.Equal("backend", fakeStorage.LastSlugQueried);
        Assert.Equal(parentId, fakeStorage.LastParentIdQueried);
    }

    [Fact]
    public async Task GetWorkspace_BySlug_ShouldNormalizeToLowercase()
    {
        // Arrange
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            WorkspaceBySlug = CreateTestWorkspace(workspaceId, "Engineering")
        };
        var tools = CreateTools(fakeStorage);

        // Act: caller passes a mixed-case, padded slug
        var result = await tools.GetWorkspace(slug: "  Engineering  ");

        // Assert: storage is queried with the canonical lowercase, trimmed slug
        Assert.Equal("engineering", fakeStorage.LastSlugQueried);
        Assert.Contains("Workspace: Engineering", result);
    }

    [Fact]
    public async Task GetWorkspace_BySlug_WhenNotFound_ShouldReturnNotFoundMessage()
    {
        // Arrange
        var fakeStorage = new FakeWorkspaceStorage
        {
            WorkspaceBySlug = null
        };
        var tools = CreateTools(fakeStorage);

        // Act
        var result = await tools.GetWorkspace(slug: "missing");

        // Assert
        Assert.Contains("Workspace with slug 'missing' not found among root workspaces.", result);
    }

    [Fact]
    public async Task GetWorkspace_BySlug_WhenNotFoundUnderParent_ShouldMentionParentScope()
    {
        // Arrange
        var parentId = new WorkspaceId(Guid.Parse("a1874a6b-8a15-4da6-a413-99bf3249d1e4"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            WorkspaceBySlug = null
        };
        var tools = CreateTools(fakeStorage);

        // Act
        var result = await tools.GetWorkspace(slug: "missing", parentWorkspaceId: parentId.Value.ToString());

        // Assert
        Assert.Contains($"Workspace with slug 'missing' not found under parent {parentId.Value}", result);
    }

    [Fact]
    public async Task GetWorkspace_QueryTakesPrecedenceOverSlug()
    {
        // Arrange
        var fakeStorage = new FakeWorkspaceStorage();
        var tools = CreateTools(fakeStorage);

        // Act
        var result = await tools.GetWorkspace(slug: "engineering", query: "eng");

        // Assert: query path was taken, so slug lookup was never invoked
        Assert.Null(fakeStorage.LastSlugQueried);
        Assert.Contains("No workspaces found matching 'eng'", result);
    }

    private static WorkspaceTools CreateTools(FakeWorkspaceStorage storage)
    {
        var logger = new NullLogger<WorkspaceTools>();
        var urlService = new FakeCanonicalUrlService { IsConfigured = false };
        return new WorkspaceTools(storage, logger, urlService);
    }

    private static Workspace CreateTestWorkspace(WorkspaceId id, string name)
    {
        return new Workspace
        {
            Id = id,
            Name = name,
            Slug = name.ToLower().Replace(" ", "-"),
            Description = null,
            IsSystem = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ParentId = null
        };
    }
}
