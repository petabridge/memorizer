using Memorizer.Models;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;

namespace Memorizer.UnitTests.Tools;

/// <summary>
/// Fake canonical URL service for workspace/project tool tests.
/// </summary>
internal class FakeCanonicalUrlService : ICanonicalUrlService
{
    public bool IsConfigured { get; set; }
    public string BaseUrl { get; set; } = "";
    public bool GetMemoryUrlCalled { get; private set; }
    public bool GetWorkspaceUrlCalled { get; private set; }
    public bool GetProjectUrlCalled { get; private set; }

    public string? GetMemoryUrl(MemoryId memoryId)
    {
        GetMemoryUrlCalled = true;
        return IsConfigured ? $"{BaseUrl.TrimEnd('/')}/view/{memoryId.Value}" : null;
    }

    public string? GetWorkspaceUrl(WorkspaceId workspaceId)
    {
        GetWorkspaceUrlCalled = true;
        return IsConfigured ? $"{BaseUrl.TrimEnd('/')}/workspaces/{workspaceId.Value}" : null;
    }

    public string? GetProjectUrl(ProjectId projectId)
    {
        GetProjectUrlCalled = true;
        return IsConfigured ? $"{BaseUrl.TrimEnd('/')}/projects/{projectId.Value}" : null;
    }
}
