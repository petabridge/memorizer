using Memorizer.Models;
using Memorizer.Models.ValueTypes;
using Memorizer.Settings;

namespace Memorizer.Services;

/// <summary>
/// Implementation of <see cref="ICanonicalUrlService"/> that generates canonical URLs
/// based on the configured Web UI base URL.
/// </summary>
public class CanonicalUrlService : ICanonicalUrlService
{
    private readonly WebUiSettings _settings;

    public CanonicalUrlService(WebUiSettings settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public bool IsConfigured => _settings.IsConfigured;

    /// <inheritdoc />
    public string? GetMemoryUrl(MemoryId memoryId)
    {
        if (!IsConfigured)
            return null;

        return $"{_settings.BaseUrl!.TrimEnd('/')}/view/{memoryId.Value}";
    }

    /// <inheritdoc />
    public string? GetWorkspaceUrl(WorkspaceId workspaceId)
    {
        if (!IsConfigured)
            return null;

        return $"{_settings.BaseUrl!.TrimEnd('/')}/workspace/{workspaceId.Value}";
    }

    /// <inheritdoc />
    public string? GetProjectUrl(ProjectId projectId)
    {
        if (!IsConfigured)
            return null;

        return $"{_settings.BaseUrl!.TrimEnd('/')}/project/{projectId.Value}";
    }
}
