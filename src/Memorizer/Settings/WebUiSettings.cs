namespace Memorizer.Settings;

/// <summary>
/// Settings for the Web UI integration.
/// Used to generate canonical URLs for resources that can be viewed in the web interface.
/// </summary>
public class WebUiSettings
{
    /// <summary>
    /// The base URL for the Memorizer web UI.
    /// Used to generate canonical URLs for memories, workspaces, and projects.
    /// Example: https://memory.testlab.petabridge.net
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Returns true if the base URL is configured and valid.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
