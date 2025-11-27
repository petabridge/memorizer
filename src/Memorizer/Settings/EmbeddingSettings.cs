namespace Memorizer.Settings;

/// <summary>
/// Settings for the embedding service.
/// Note: Embedding dimensions are auto-detected from the model and stored in the database.
/// See IEmbeddingDimensionService for dimension management.
/// </summary>
public class EmbeddingSettings
{
    public required Uri ApiUrl { get; init; }
    public required string Model { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}