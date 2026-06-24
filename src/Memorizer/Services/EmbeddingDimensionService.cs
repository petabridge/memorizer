using Memorizer.Models;
using Memorizer.Settings;
using Microsoft.Extensions.Options;
using Npgsql;
using Registrator.Net;

namespace Memorizer.Services;

/// <summary>
/// Service for validating and managing embedding dimensions.
/// Detects mismatches between configured model, actual output, and database schema.
///
/// Uses IOptionsSnapshot for reloadable configuration - register as Scoped
/// to get fresh settings on each request scope.
/// </summary>
public class EmbeddingDimensionService : IEmbeddingDimensionService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IOptionsSnapshot<EmbeddingSettings> _settingsSnapshot;
    private readonly IEmbeddingApiClient _apiClient;
    private readonly ILogger<EmbeddingDimensionService> _logger;

    // Convenience property to get current settings
    private EmbeddingSettings Settings => _settingsSnapshot.Value;

    // Cache the probed dimensions to avoid repeated API calls
    private int? _cachedModelDimensions;
    private string? _cachedModelName;

    public EmbeddingDimensionService(
        NpgsqlDataSource dataSource,
        IOptionsSnapshot<EmbeddingSettings> settingsSnapshot,
        IEmbeddingApiClient apiClient,
        ILogger<EmbeddingDimensionService> logger)
    {
        _dataSource = dataSource;
        _settingsSnapshot = settingsSnapshot;
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task<int?> ProbeModelDimensionsAsync(CancellationToken ct = default)
    {
        if (_cachedModelDimensions.HasValue && _cachedModelName == Settings.Model)
        {
            return _cachedModelDimensions;
        }

        try
        {
            _logger.LogDebug("Probing embedding model {Model} for dimensions", Settings.Model);

            var embedding = await _apiClient.GenerateAsync(Settings.Model, "dimension probe", ct);

            if (embedding.Length == 0)
            {
                _logger.LogWarning("Empty embedding response from model probe");
                return null;
            }

            _cachedModelDimensions = embedding.Length;
            _cachedModelName = Settings.Model;

            _logger.LogInformation("Detected {Dimensions} dimensions from model {Model}",
                _cachedModelDimensions, Settings.Model);

            return _cachedModelDimensions;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe embedding model: {Message}", ex.Message);
            return null;
        }
    }

    public async Task<EmbeddingConfigRecord?> GetActiveConfigAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT id, model_name, dimensions, detected_at, is_active
            FROM embedding_config
            WHERE is_active = true
            LIMIT 1";

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                return new EmbeddingConfigRecord
                {
                    Id = reader.GetInt32(0),
                    ModelName = reader.GetString(1),
                    Dimensions = reader.GetInt32(2),
                    DetectedAt = reader.GetDateTime(3),
                    IsActive = reader.GetBoolean(4)
                };
            }

            return null;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table doesn't exist
        {
            _logger.LogDebug("embedding_config table doesn't exist yet (migrations not run)");
            return null;
        }
    }

    public async Task<int?> GetDatabaseSchemaDimensionsAsync(CancellationToken ct = default)
    {
        // pgvector stores dimension in atttypmod for VECTOR(n) types
        const string sql = @"
            SELECT a.atttypmod
            FROM pg_attribute a
            JOIN pg_class c ON a.attrelid = c.oid
            JOIN pg_namespace n ON c.relnamespace = n.oid
            JOIN pg_type t ON a.atttypid = t.oid
            WHERE c.relname = 'memories'
              AND a.attname = 'embedding'
              AND n.nspname = 'public'
              AND t.typname = 'vector'";

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            var result = await cmd.ExecuteScalarAsync(ct);

            if (result is int atttypmod && atttypmod > 0)
            {
                _logger.LogDebug("Database schema has VECTOR({Dimensions})", atttypmod);
                return atttypmod;
            }

            _logger.LogDebug("Could not determine VECTOR column dimensions from schema");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query database schema dimensions: {Message}", ex.Message);
            return null;
        }
    }

    public async Task<DimensionValidationResult> ValidateAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Validating embedding dimensions for model {Model}", Settings.Model);

        // Gather all sources of truth
        var detectedDimensions = await ProbeModelDimensionsAsync(ct);
        var storedConfig = await GetActiveConfigAsync(ct);
        var schemaDimensions = await GetDatabaseSchemaDimensionsAsync(ct);

        var embeddingApiAvailable = detectedDimensions.HasValue;

        // Check for various mismatch scenarios
        // Priority: Schema dimensions are the source of truth for what's actually stored in the database

        // Scenario 1: Schema mismatch (detected dimensions differ from schema)
        // This is the PRIMARY check - if model output matches schema, embeddings will work
        if (detectedDimensions.HasValue && schemaDimensions.HasValue &&
            detectedDimensions.Value != schemaDimensions.Value)
        {
            var description = $"Model '{Settings.Model}' outputs {detectedDimensions} dimensions, " +
                              $"but database schema has VECTOR({schemaDimensions}).";

            _logger.LogWarning("Dimension mismatch: {Description}", description);

            return DimensionValidationResult.Mismatch(
                configuredModel: Settings.Model,
                detectedDimensions: detectedDimensions,
                storedDimensions: storedConfig?.Dimensions,
                storedModel: storedConfig?.ModelName,
                schemaDimensions: schemaDimensions,
                description: description,
                embeddingApiAvailable: embeddingApiAvailable);
        }

        // Scenario 3: Stored config doesn't match schema.
        // If live probe matches schema, this is stale metadata and we can self-heal.
        if (storedConfig != null && schemaDimensions.HasValue &&
            storedConfig.Dimensions != schemaDimensions.Value)
        {
            if (detectedDimensions.HasValue && detectedDimensions.Value == schemaDimensions.Value)
            {
                _logger.LogInformation(
                    "Stored config is stale (stored={StoredDimensions}, schema={SchemaDimensions}, model={StoredModel}). " +
                    "Live probe matches schema ({DetectedDimensions}), updating stored config.",
                    storedConfig.Dimensions, schemaDimensions, storedConfig.ModelName, detectedDimensions);

                await UpdateActiveConfigAsync(Settings.Model, detectedDimensions.Value, ct);
                storedConfig = await GetActiveConfigAsync(ct);
            }
            else
            {
                var description = $"Stored config shows {storedConfig.Dimensions} dimensions, " +
                                  $"but database schema has VECTOR({schemaDimensions}). " +
                                  "This may indicate an incomplete migration.";

                _logger.LogWarning("Dimension mismatch: {Description}", description);

                return DimensionValidationResult.Mismatch(
                    configuredModel: Settings.Model,
                    detectedDimensions: detectedDimensions,
                    storedDimensions: storedConfig.Dimensions,
                    storedModel: storedConfig.ModelName,
                    schemaDimensions: schemaDimensions,
                    description: description,
                    embeddingApiAvailable: embeddingApiAvailable);
            }
        }

        // Scenario 2: Model name changed but dimensions match schema - update stored config
        if (storedConfig != null && storedConfig.ModelName != Settings.Model)
        {
            // If detected dimensions match the schema, update the stored config to the new model
            if (detectedDimensions.HasValue && schemaDimensions.HasValue &&
                detectedDimensions.Value == schemaDimensions.Value)
            {
                _logger.LogInformation(
                    "Model changed from '{OldModel}' to '{NewModel}', dimensions match schema ({Dimensions}). " +
                    "Updating stored config.",
                    storedConfig.ModelName, Settings.Model, detectedDimensions);

                // Update the config to reflect the new model
                await UpdateActiveConfigAsync(Settings.Model, detectedDimensions.Value, ct);
            }
        }

        // All checks passed - no mismatch
        var effectiveDimensions = detectedDimensions
                                  ?? storedConfig?.Dimensions
                                  ?? schemaDimensions
                                  ?? 384;

        _logger.LogInformation("Dimension validation passed. Effective dimensions: {Dimensions}", effectiveDimensions);

        return DimensionValidationResult.Success(
            configuredModel: Settings.Model,
            dimensions: effectiveDimensions,
            embeddingApiAvailable: embeddingApiAvailable);
    }

    public async Task UpdateActiveConfigAsync(string modelName, int dimensions, CancellationToken ct = default)
    {
        _logger.LogInformation("Updating active embedding config: model={Model}, dimensions={Dimensions}",
            modelName, dimensions);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            // Deactivate existing config
            const string deactivateSql = "UPDATE embedding_config SET is_active = false WHERE is_active = true";
            await using (var cmd = new NpgsqlCommand(deactivateSql, conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Insert new active config
            const string insertSql = @"
                INSERT INTO embedding_config (model_name, dimensions, detected_at, is_active)
                VALUES (@modelName, @dimensions, NOW(), true)";

            await using (var cmd = new NpgsqlCommand(insertSql, conn, transaction))
            {
                cmd.Parameters.AddWithValue("modelName", modelName);
                cmd.Parameters.AddWithValue("dimensions", dimensions);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);

            // Update cache
            _cachedModelDimensions = dimensions;
            _cachedModelName = modelName;

            _logger.LogInformation("Successfully updated embedding config");
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> GetEffectiveDimensionsAsync(CancellationToken ct = default)
    {
        // Try detected first (most accurate)
        var detected = await ProbeModelDimensionsAsync(ct);
        if (detected.HasValue)
            return detected.Value;

        // Fall back to stored config
        var stored = await GetActiveConfigAsync(ct);
        if (stored != null)
            return stored.Dimensions;

        // Fall back to schema
        var schema = await GetDatabaseSchemaDimensionsAsync(ct);
        if (schema.HasValue)
            return schema.Value;

        // Default fallback
        return 384;
    }
}
