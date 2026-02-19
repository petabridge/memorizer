using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;

namespace Memorizer.UnitTests;

public class CanonicalUrlServiceTests
{
    private readonly CanonicalUrlService _configuredService;
    private readonly CanonicalUrlService _unconfiguredService;

    public CanonicalUrlServiceTests()
    {
        var configuredSettings = new WebUiSettings
        {
            BaseUrl = "https://memory.testlab.petabridge.net"
        };
        _configuredService = new CanonicalUrlService(configuredSettings);

        var unconfiguredSettings = new WebUiSettings();
        _unconfiguredService = new CanonicalUrlService(unconfiguredSettings);
    }

    [Fact]
    public void IsConfigured_ShouldReturnTrue_WhenBaseUrlIsSet()
    {
        Assert.True(_configuredService.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ShouldReturnFalse_WhenBaseUrlIsNull()
    {
        Assert.False(_unconfiguredService.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ShouldReturnFalse_WhenBaseUrlIsEmpty()
    {
        var settings = new WebUiSettings { BaseUrl = "" };
        var service = new CanonicalUrlService(settings);
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ShouldReturnFalse_WhenBaseUrlIsWhitespace()
    {
        var settings = new WebUiSettings { BaseUrl = "   " };
        var service = new CanonicalUrlService(settings);
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public void GetMemoryUrl_ShouldReturnCorrectUrl_WhenConfigured()
    {
        var memoryId = new MemoryId(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));
        var url = _configuredService.GetMemoryUrl(memoryId);
        Assert.Equal("https://memory.testlab.petabridge.net/view/a1b2c3d4-e5f6-7890-abcd-ef1234567890", url);
    }

    [Fact]
    public void GetMemoryUrl_ShouldReturnNull_WhenNotConfigured()
    {
        var memoryId = new MemoryId(Guid.NewGuid());
        var url = _unconfiguredService.GetMemoryUrl(memoryId);
        Assert.Null(url);
    }

    [Fact]
    public void GetWorkspaceUrl_ShouldReturnCorrectUrl_WhenConfigured()
    {
        var workspaceId = new WorkspaceId(Guid.Parse("b775bb37-4af5-46fe-ad14-7f6fba7889aa"));
        var url = _configuredService.GetWorkspaceUrl(workspaceId);
        Assert.Equal("https://memory.testlab.petabridge.net/workspace/b775bb37-4af5-46fe-ad14-7f6fba7889aa", url);
    }

    [Fact]
    public void GetWorkspaceUrl_ShouldReturnNull_WhenNotConfigured()
    {
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var url = _unconfiguredService.GetWorkspaceUrl(workspaceId);
        Assert.Null(url);
    }

    [Fact]
    public void GetProjectUrl_ShouldReturnCorrectUrl_WhenConfigured()
    {
        var projectId = new ProjectId(Guid.Parse("a1874a6b-8a15-4da6-a413-99bf3249d1e4"));
        var url = _configuredService.GetProjectUrl(projectId);
        Assert.Equal("https://memory.testlab.petabridge.net/project/a1874a6b-8a15-4da6-a413-99bf3249d1e4", url);
    }

    [Fact]
    public void GetProjectUrl_ShouldReturnNull_WhenNotConfigured()
    {
        var projectId = new ProjectId(Guid.NewGuid());
        var url = _unconfiguredService.GetProjectUrl(projectId);
        Assert.Null(url);
    }

    [Theory]
    [InlineData("https://memory.testlab.petabridge.net/")]
    [InlineData("https://memory.testlab.petabridge.net")]
    [InlineData("https://memory.testlab.petabridge.net///")]
    public void GetMemoryUrl_ShouldHandleTrailingSlashes_Correctly(string baseUrl)
    {
        var settings = new WebUiSettings { BaseUrl = baseUrl };
        var service = new CanonicalUrlService(settings);
        var memoryId = new MemoryId(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));

        var url = service.GetMemoryUrl(memoryId);

        Assert.Equal("https://memory.testlab.petabridge.net/view/a1b2c3d4-e5f6-7890-abcd-ef1234567890", url);
    }
}
