namespace Memorizer.Settings;

/// <summary>
/// Settings for the MCP search tool behavior.
/// </summary>
public class SearchSettings
{
    /// <summary>
    /// When true, search results include the full memory content/body text.
    /// When false (default), search returns lightweight results (ID, title, type, tags, similarity)
    /// and agents should use Get/GetMany to retrieve full content.
    ///
    /// Default is false to optimize context window usage for LLM agents.
    /// </summary>
    public bool ReturnFullContent { get; init; } = false;
}
