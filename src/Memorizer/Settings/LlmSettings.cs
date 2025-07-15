namespace Memorizer.Settings;

/// <summary>
/// Settings for LLM (Large Language Model) services
/// </summary>
public sealed class LlmSettings
{
    /// <summary>
    /// API URL for the LLM service (e.g., Ollama)
    /// </summary>
    public Uri ApiUrl { get; set; } = new("http://localhost:11434");

    /// <summary>
    /// Model name to use for LLM operations
    /// </summary>
    public string Model { get; set; } = "llama3";

    /// <summary>
    /// Timeout for LLM requests
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
} 