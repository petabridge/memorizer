namespace Memorizer.Models;

/// <summary>
/// Represents the currently active embedding configuration stored in the database.
/// Tracks which embedding model is in use and its output dimensions.
/// </summary>
public class EmbeddingConfigRecord
{
    /// <summary>
    /// Database ID
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// The embedding model name (e.g., "all-minilm:33m-l12-v2-fp16", "nomic-embed-text:v1.5")
    /// </summary>
    public required string ModelName { get; init; }

    /// <summary>
    /// The number of dimensions the model outputs
    /// </summary>
    public int Dimensions { get; init; }

    /// <summary>
    /// When this configuration was detected/created
    /// </summary>
    public DateTime DetectedAt { get; init; }

    /// <summary>
    /// Whether this is the currently active configuration
    /// </summary>
    public bool IsActive { get; init; }
}
