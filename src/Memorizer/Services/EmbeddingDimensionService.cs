using Memorizer.Models;
using Memorizer.Settings;
using Npgsql;
using Registrator.Net;

namespace Memorizer.Services;

/// <summary>
/// Service for validating and managing embedding dimensions.
/// Detects mismatches between configured model, actual output, and database schema.
/// Note: Explicitly registered in ServiceCollectionExtensions with HttpClient configuration.
/// </summary>
public class EmbeddingDimensionService : IEmbeddingDimensionService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly EmbeddingSettings _embeddingSettings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<EmbeddingDimensionService> _logger;

    // Cache the probed dimensions to avoid repeated API calls
    private int? _cachedModelDimensions;
    private string? _cachedModelName;

    public EmbeddingDimensionService(
        NpgsqlDataSource dataSource,
        EmbeddingSettings embeddingSettings,
        HttpClient httpClient,
        ILogger<EmbeddingDimensionService> logger)
    {
        _dataSource = dataSource;
        _embeddingSettings = embeddingSettings;
        _httpClient = httpClient;
        _httpClient.BaseAddress = embeddingSettings.ApiUrl;
        _httpClient.Timeout = embeddingSettings.Timeout;
        _logger = logger;
    }

    public async Task<int?> ProbeModelDimensionsAsync(CancellationToken ct = default)
    {
        // Return cached value if we've already probed this model
        if (_cachedModelDimensions.HasValue && _cachedModelName == _embeddingSettings.Model)
        {
            return _cachedModelDimensions;
        }

        try
        {
            _logger.LogDebug("Probing embedding model {Model} for dimensions", _embeddingSettings.Model);

            var request = new EmbeddingRequest
            {
                Model = _embeddingSettings.Model,
                Prompt = "dimension probe"
            };

            var response = await _httpClient.PostAsJsonAsync("api/embeddings", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct);

            if (result?.Embedding == null || result.Embedding.Length == 0)
            {
                _logger.LogWarning("Empty embedding response from model probe");
                return null;
            }

            _cachedModelDimensions = result.Embedding.Length;
            _cachedModelName = _embeddingSettings.Model;

            _logger.LogInformation("Detected {Dimensions} dimensions from model {Model}",
                _cachedModelDimensions, _embeddingSettings.Model);

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
        _logger.LogInformation("Validating embedding dimensions for model {Model}", _embeddingSettings.Model);

        // Gather all sources of truth
        var detectedDimensions = await ProbeModelDimensionsAsync(ct);
        var storedConfig = await GetActiveConfigAsync(ct);
        var schemaDimensions = await GetDatabaseSchemaDimensionsAsync(ct);

        var embeddingApiAvailable = detectedDimensions.HasValue;

        // Check for various mismatch scenarios

        // Scenario 1: Model changed (detected dimensions differ from stored)
        if (detectedDimensions.HasValue && storedConfig != null &&
            detectedDimensions.Value != storedConfig.Dimensions)
        {
            var description = $"Model '{_embeddingSettings.Model}' outputs {detectedDimensions} dimensions, " +
                              $"but stored config for '{storedConfig.ModelName}' expects {storedConfig.Dimensions} dimensions.";

            _logger.LogWarning("Dimension mismatch: {Description}", description);

            return DimensionValidationResult.Mismatch(
                configuredModel: _embeddingSettings.Model,
                detectedDimensions: detectedDimensions,
                storedDimensions: storedConfig.Dimensions,
                storedModel: storedConfig.ModelName,
                schemaDimensions: schemaDimensions,
                description: description,
                embeddingApiAvailable: embeddingApiAvailable);
        }

        // Scenario 2: Schema mismatch (detected dimensions differ from schema)
        if (detectedDimensions.HasValue && schemaDimensions.HasValue &&
            detectedDimensions.Value != schemaDimensions.Value)
        {
            var description = $"Model '{_embeddingSettings.Model}' outputs {detectedDimensions} dimensions, " +
                              $"but database schema has VECTOR({schemaDimensions}).";

            _logger.LogWarning("Dimension mismatch: {Description}", description);

            return DimensionValidationResult.Mismatch(
                configuredModel: _embeddingSettings.Model,
                detectedDimensions: detectedDimensions,
                storedDimensions: storedConfig?.Dimensions,
                storedModel: storedConfig?.ModelName,
                schemaDimensions: schemaDimensions,
                description: description,
                embeddingApiAvailable: embeddingApiAvailable);
        }

        // Scenario 3: Stored config doesn't match schema (could happen after failed migration)
        if (storedConfig != null && schemaDimensions.HasValue &&
            storedConfig.Dimensions != schemaDimensions.Value)
        {
            var description = $"Stored config shows {storedConfig.Dimensions} dimensions, " +
                              $"but database schema has VECTOR({schemaDimensions}). " +
                              "This may indicate an incomplete migration.";

            _logger.LogWarning("Dimension mismatch: {Description}", description);

            return DimensionValidationResult.Mismatch(
                configuredModel: _embeddingSettings.Model,
                detectedDimensions: detectedDimensions,
                storedDimensions: storedConfig.Dimensions,
                storedModel: storedConfig.ModelName,
                schemaDimensions: schemaDimensions,
                description: description,
                embeddingApiAvailable: embeddingApiAvailable);
        }

        // Scenario 4: Model name changed but dimensions are same (just update config)
        if (storedConfig != null && storedConfig.ModelName != _embeddingSettings.Model)
        {
            // If we have detected dimensions and they match, just update the config
            if (detectedDimensions.HasValue && detectedDimensions.Value == storedConfig.Dimensions)
            {
                _logger.LogInformation(
                    "Model name changed from '{OldModel}' to '{NewModel}' but dimensions match ({Dimensions}). " +
                    "Config will be updated on next successful operation.",
                    storedConfig.ModelName, _embeddingSettings.Model, detectedDimensions);
            }
        }

        // All checks passed - no mismatch
        var effectiveDimensions = detectedDimensions
                                  ?? storedConfig?.Dimensions
                                  ?? schemaDimensions
                                  ?? 384;

        _logger.LogInformation("Dimension validation passed. Effective dimensions: {Dimensions}", effectiveDimensions);

        return DimensionValidationResult.Success(
            configuredModel: _embeddingSettings.Model,
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
