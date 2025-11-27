namespace Memorizer.Models;

/// <summary>
/// Result of validating embedding dimensions across config, model output, and database schema.
/// </summary>
public record DimensionValidationResult(
    /// <summary>
    /// The configured embedding model name from appsettings
    /// </summary>
    string ConfiguredModel,

    /// <summary>
    /// Dimensions detected by probing the embedding model (null if API unavailable)
    /// </summary>
    int? DetectedModelDimensions,

    /// <summary>
    /// Dimensions stored in embedding_config table (null if no config stored yet)
    /// </summary>
    int? StoredDimensions,

    /// <summary>
    /// Model name stored in embedding_config table
    /// </summary>
    string? StoredModel,

    /// <summary>
    /// Actual VECTOR column dimensions from PostgreSQL schema
    /// </summary>
    int? DatabaseSchemaDimensions,

    /// <summary>
    /// Whether any mismatch was detected
    /// </summary>
    bool HasMismatch,

    /// <summary>
    /// Human-readable description of the mismatch (null if no mismatch)
    /// </summary>
    string? MismatchDescription,

    /// <summary>
    /// Whether migration is required to fix the mismatch
    /// </summary>
    bool RequiresMigration,

    /// <summary>
    /// Whether the embedding API was available during validation
    /// </summary>
    bool EmbeddingApiAvailable
)
{
    /// <summary>
    /// The effective dimensions to use (detected > stored > schema > default)
    /// </summary>
    public int EffectiveDimensions =>
        DetectedModelDimensions
        ?? StoredDimensions
        ?? DatabaseSchemaDimensions
        ?? 384;

    /// <summary>
    /// Creates a successful validation result with no mismatches
    /// </summary>
    public static DimensionValidationResult Success(
        string configuredModel,
        int dimensions,
        bool embeddingApiAvailable = true) => new(
        ConfiguredModel: configuredModel,
        DetectedModelDimensions: embeddingApiAvailable ? dimensions : null,
        StoredDimensions: dimensions,
        StoredModel: configuredModel,
        DatabaseSchemaDimensions: dimensions,
        HasMismatch: false,
        MismatchDescription: null,
        RequiresMigration: false,
        EmbeddingApiAvailable: embeddingApiAvailable
    );

    /// <summary>
    /// Creates a mismatch result requiring migration
    /// </summary>
    public static DimensionValidationResult Mismatch(
        string configuredModel,
        int? detectedDimensions,
        int? storedDimensions,
        string? storedModel,
        int? schemaDimensions,
        string description,
        bool embeddingApiAvailable = true) => new(
        ConfiguredModel: configuredModel,
        DetectedModelDimensions: detectedDimensions,
        StoredDimensions: storedDimensions,
        StoredModel: storedModel,
        DatabaseSchemaDimensions: schemaDimensions,
        HasMismatch: true,
        MismatchDescription: description,
        RequiresMigration: true,
        EmbeddingApiAvailable: embeddingApiAvailable
    );
}
