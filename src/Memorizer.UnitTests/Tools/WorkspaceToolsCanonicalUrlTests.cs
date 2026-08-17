using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Microsoft.Extensions.Logging.Abstractions;
using PostgMem.Tools;

namespace Memorizer.UnitTests.Tools;

/// <summary>
/// Tests that verify WorkspaceTools MCP responses include canonical URLs when configured.
/// Uses manual fakes instead of mocking framework.
/// </summary>
public class WorkspaceToolsCanonicalUrlTests
{
    private const string TestCanonicalUrl = "https://memory.testlab.petabridge.net";

    [Fact]
    public async Task GetWorkspace_ById_ShouldIncludeCanonicalUrl_WhenConfigured()
    {
        // Arrange
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            Workspace = CreateTestWorkspace(workspaceId, "Test Workspace")
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = true,
            BaseUrl = TestCanonicalUrl
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.GetWorkspace(workspaceId.Value.ToString());

        // Assert
        Assert.Contains(TestCanonicalUrl, result);
        Assert.Contains("/workspaces/b775bb37-4af5-46fe-ad14-7f6fba7889aa", result);
        Assert.True(fakeUrlService.GetWorkspaceUrlCalled);
    }

    [Fact]
    public async Task GetWorkspace_ById_ShouldNotIncludeCanonicalUrl_WhenNotConfigured()
    {
        // Arrange
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            Workspace = CreateTestWorkspace(workspaceId, "Test Workspace")
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = false
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.GetWorkspace(workspaceId.Value.ToString());

        // Assert
        Assert.DoesNotContain(TestCanonicalUrl, result);
        Assert.DoesNotContain("URL:", result);
    }

    [Fact]
    public async Task CreateWorkspace_ShouldIncludeCanonicalUrl_WhenConfigured()
    {
        // Arrange
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            CreatedWorkspace = CreateTestWorkspace(workspaceId, "New Workspace")
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = true,
            BaseUrl = TestCanonicalUrl
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.CreateWorkspace("New Workspace");

        // Assert
        Assert.Contains(TestCanonicalUrl, result);
        Assert.Contains("/workspaces/b775bb37-4af5-46fe-ad14-7f6fba7889aa", result);
        Assert.True(fakeUrlService.GetWorkspaceUrlCalled);
    }

    [Fact]
    public async Task CreateWorkspace_ShouldNotIncludeCanonicalUrl_WhenNotConfigured()
    {
        // Arrange
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            CreatedWorkspace = CreateTestWorkspace(workspaceId, "New Workspace")
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = false
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.CreateWorkspace("New Workspace");

        // Assert
        Assert.DoesNotContain(TestCanonicalUrl, result);
        Assert.DoesNotContain("URL:", result);
    }

    [Fact]
    public async Task GetProjectContext_ById_ShouldIncludeCanonicalUrl_WhenConfigured()
    {
        // Arrange
        var projectId = new ProjectId(Guid.Parse("a1874a6b-8a15-4da6-a413-99bf3249d1e4"));
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            Project = CreateTestProject(projectId, workspaceId, "Test Project")
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = true,
            BaseUrl = TestCanonicalUrl
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.GetProjectContext(projectId.Value.ToString());

        // Assert
        Assert.Contains(TestCanonicalUrl, result);
        Assert.Contains("/projects/a1874a6b-8a15-4da6-a413-99bf3249d1e4", result);
        Assert.True(fakeUrlService.GetProjectUrlCalled);
    }

    [Fact]
    public async Task CreateProject_ShouldIncludeCanonicalUrl_WhenConfigured()
    {
        // Arrange
        var projectId = new ProjectId(Guid.Parse("a1874a6b-8a15-4da6-a413-99bf3249d1e4"));
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var fakeStorage = new FakeWorkspaceStorage
        {
            CreatedProject = CreateTestProject(projectId, workspaceId, "New Project"),
            Workspace = CreateTestWorkspace(workspaceId, "Test Workspace")
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = true,
            BaseUrl = TestCanonicalUrl
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.CreateProject(workspaceId.Value, "New Project");

        // Assert
        Assert.Contains(TestCanonicalUrl, result);
        Assert.Contains("/projects/a1874a6b-8a15-4da6-a413-99bf3249d1e4", result);
        Assert.True(fakeUrlService.GetProjectUrlCalled);
    }

    private static WorkspaceTools CreateTools(FakeWorkspaceStorage storage, FakeCanonicalUrlService urlService)
    {
        var logger = new NullLogger<WorkspaceTools>();
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

    private static Project CreateTestProject(ProjectId id, WorkspaceId workspaceId, string name)
    {
        return new Project
        {
            Id = id,
            WorkspaceId = workspaceId,
            Name = name,
            Slug = name.ToLower().Replace(" ", "-"),
            Description = null,
            Status = ProjectStatusEnum.Active,
            ParentId = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            VictoryConditions = null
        };
    }
}
