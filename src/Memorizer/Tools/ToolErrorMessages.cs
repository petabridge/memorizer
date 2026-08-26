using Memorizer.Services;

namespace Memorizer.Tools;

/// <summary>
/// Builds honest, caller-facing messages for tool failures that happen after argument
/// validation — currently, when a write could not be saved because its search embedding
/// could not be generated.
/// </summary>
public static class ToolErrorMessages
{
    // Substrings a backend uses when the input exceeded the model's context window. We
    // cannot tokenize in-process, so we classify from the backend's own error text and
    // otherwise fall back to a neutral message.
    private static readonly string[] LengthIndicators =
    [
        "context length", "context window", "too long", "maximum context",
        "exceeds", "max_tokens", "input is too large", "maximum number of tokens",
    ];

    /// <summary>
    /// Message for when a tool could not complete because the embedding backend failed.
    /// States plainly that nothing was saved (Memorizer will not store a memory that
    /// would be invisible to search — see #215), and gives the most actionable remedy we
    /// can determine, passing the backend's own reason through so "how far over" is
    /// surfaced when the backend reports it.
    /// </summary>
    public static string EmbeddingUnavailable(string toolName, EmbeddingGenerationException ex)
    {
        var backend = ex.InnerException?.Message?.Trim();
        var looksTooLong = !string.IsNullOrEmpty(backend)
            && LengthIndicators.Any(k => backend.Contains(k, StringComparison.OrdinalIgnoreCase));

        var remedy = looksTooLong
            ? $"The text is too long for embedding model '{ex.Model}' — it was {ex.InputLength} characters. Shorten it and try again."
            : $"The embedding backend (model '{ex.Model}') looks unavailable. If the text is very long ({ex.InputLength} characters) shorten it; otherwise retry shortly.";

        var detail = string.IsNullOrEmpty(backend) ? string.Empty : $" Backend reported: {backend}";

        return $"{toolName}: not saved. Its search embedding could not be generated, so nothing was written "
             + $"— Memorizer will not store a memory that would be invisible to search. {remedy}{detail}";
    }
}
