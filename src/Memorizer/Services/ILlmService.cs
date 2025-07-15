using Memorizer.Models;

namespace Memorizer.Services;

/// <summary>
/// Service for interacting with Large Language Models for text analysis
/// </summary>
public interface ILlmService : IDisposable
{
    /// <summary>
    /// Tests connectivity to the LLM service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating if the service is available and configured</returns>
    Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Generates a descriptive title for content that doesn't have one
    /// </summary>
    /// <param name="content">The text content to generate a title for</param>
    /// <param name="contentType">Type of content (e.g., "reference", "how-to")</param>
    /// <param name="existingTags">Existing tags for context</param>
    /// <param name="maxTitleLength">Maximum length for the generated title</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Generated title</returns>
    Task<string> GenerateTitle(
        string content,
        string contentType,
        string[]? existingTags = null,
        int maxTitleLength = 80,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Result of LLM health check
/// </summary>
public class LlmHealthResult
{
    public bool IsHealthy { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ModelName { get; set; }
    public TimeSpan? ResponseTime { get; set; }
    public string? ErrorDetails { get; set; }
} 