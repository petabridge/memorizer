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
    private readonly ServerSettings _settings;

    public CanonicalUrlService(ServerSettings settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.CanonicalUrl);

    private string BaseUrl => _settings.CanonicalUrl!;

    /// <inheritdoc />
    public string? GetMemoryUrl(MemoryId memoryId)
    {
        if (!IsConfigured)
            return null;

        return $"{BaseUrl.TrimEnd('/')}/view/{memoryId.Value}";
    }

    /// <inheritdoc />
    public string? GetWorkspaceUrl(WorkspaceId workspaceId)
    {
        if (!IsConfigured)
            return null;

        return $"{BaseUrl.TrimEnd('/')}/workspace/{workspaceId.Value}";
    }

    /// <inheritdoc />
    public string? GetProjectUrl(ProjectId projectId)
    {
        if (!IsConfigured)
            return null;

        return $"{BaseUrl.TrimEnd('/')}/project/{projectId.Value}";
    }
}
