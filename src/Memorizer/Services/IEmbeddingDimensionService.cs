using Memorizer.Models;

namespace Memorizer.Services;

/// <summary>
/// Service for validating and managing embedding dimensions.
/// Compares configured model, actual model output, and database schema to detect mismatches.
/// </summary>
public interface IEmbeddingDimensionService
{
    /// <summary>
    /// Probe the embedding model with a test string to discover its output dimensions.
    /// Returns null if the embedding API is unavailable.
    /// </summary>
    Task<int?> ProbeModelDimensionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the currently active embedding configuration from the database.
    /// Returns null if no configuration has been stored yet.
    /// </summary>
    Task<EmbeddingConfigRecord?> GetActiveConfigAsync(CancellationToken ct = default);

    /// <summary>
    /// Get actual VECTOR column dimensions from the PostgreSQL schema.
    /// Returns null if the table doesn't exist or column is not a vector type.
    /// </summary>
    Task<int?> GetDatabaseSchemaDimensionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Perform full validation of embedding dimensions across all sources:
    /// - Configured model from appsettings
    /// - Actual model output (via probe)
    /// - Stored configuration in database
    /// - PostgreSQL schema
    /// </summary>
    Task<DimensionValidationResult> ValidateAsync(CancellationToken ct = default);

    /// <summary>
    /// Update the active embedding configuration after a successful migration.
    /// Deactivates any existing config and creates a new active one.
    /// </summary>
    Task UpdateActiveConfigAsync(string modelName, int dimensions, CancellationToken ct = default);

    /// <summary>
    /// Get the effective dimensions to use for fallback/default scenarios.
    /// Uses detected dimensions first. If stored config and schema disagree,
    /// schema wins so fallback embeddings can still be written to VECTOR(n) columns.
    /// </summary>
    Task<int> GetEffectiveDimensionsAsync(CancellationToken ct = default);
}
