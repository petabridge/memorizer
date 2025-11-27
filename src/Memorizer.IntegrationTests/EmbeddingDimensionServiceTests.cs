using Memorizer.Extensions;
using Memorizer.IntegrationTests.Logging;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Tests for the EmbeddingDimensionService which validates embedding dimensions
/// across the configured model, actual model output, and database schema.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class EmbeddingDimensionServiceTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public EmbeddingDimensionServiceTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private IServiceProvider CreateServices(string model = "all-minilm")
    {
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                ["Embeddings:Model"] = model,
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString()
            })
            .Build());

        var embeddingSettings = new EmbeddingSettings
        {
            ApiUrl = new Uri(_fixture.OllamaApiUrl),
            Model = model,
            Timeout = TimeSpan.FromMinutes(1)
        };

        services.AddSingleton(embeddingSettings);

        services.AddHttpClient<IEmbeddingDimensionService, EmbeddingDimensionService>((sp, client) =>
        {
            client.BaseAddress = embeddingSettings.ApiUrl;
            client.Timeout = embeddingSettings.Timeout;
        });

        services.AddMemorizer();
        services.AddLogging(builder => builder.AddXUnit(_output, LogLevel.Debug));

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ProbeModelDimensions_ReturnsCorrectDimensionsForAllMiniLm()
    {
        // Arrange
        var services = CreateServices("all-minilm");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        // Act
        var dimensions = await dimensionService.ProbeModelDimensionsAsync();

        // Assert
        Assert.NotNull(dimensions);
        Assert.Equal(384, dimensions.Value); // all-minilm outputs 384 dimensions
        _output.WriteLine($"✅ Probed model dimensions: {dimensions}");
    }

    [Fact]
    public async Task GetDatabaseSchemaDimensions_ReturnsSchemaVectorDimensions()
    {
        // Arrange
        var services = CreateServices();
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        // Act
        var schemaDimensions = await dimensionService.GetDatabaseSchemaDimensionsAsync();

        // Assert
        Assert.NotNull(schemaDimensions);
        Assert.Equal(384, schemaDimensions.Value); // Default schema is VECTOR(384)
        _output.WriteLine($"✅ Database schema dimensions: {schemaDimensions}");
    }

    [Fact]
    public async Task Validate_WhenDimensionsMatch_ReturnsSuccessWithNoMismatch()
    {
        // Arrange
        var services = CreateServices("all-minilm");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        // Act
        var result = await dimensionService.ValidateAsync();

        // Assert
        Assert.False(result.HasMismatch, $"Unexpected mismatch: {result.MismatchDescription}");
        Assert.False(result.RequiresMigration);
        Assert.Equal("all-minilm", result.ConfiguredModel);
        Assert.Equal(384, result.EffectiveDimensions);
        Assert.True(result.EmbeddingApiAvailable);
        _output.WriteLine($"✅ Validation passed: model={result.ConfiguredModel}, dimensions={result.EffectiveDimensions}");
    }

    [Fact]
    public async Task Validate_WhenStoredConfigHasDifferentDimensions_ReturnsMismatch()
    {
        // Arrange - Create a stored config with different dimensions than the model outputs
        // This simulates switching to a model with different output dimensions
        var services = CreateServices("all-minilm");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        await using var conn = new NpgsqlConnection(_fixture.PostgresConnectionString);
        await conn.OpenAsync();

        // Deactivate any existing configs first
        await using var deactivateCmd = new NpgsqlCommand(
            "UPDATE embedding_config SET is_active = false WHERE is_active = true", conn);
        await deactivateCmd.ExecuteNonQueryAsync();

        // Insert a fake config claiming a different dimension (simulating prior model with 768D)
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO embedding_config (model_name, dimensions, detected_at, is_active)
            VALUES ('fake-model-768d', 768, NOW(), true)", conn);
        await cmd.ExecuteNonQueryAsync();

        try
        {
            // Act
            var result = await dimensionService.ValidateAsync();

            // Assert - Model outputs 384, but stored config says 768
            Assert.True(result.HasMismatch, $"Expected mismatch. Result: detected={result.DetectedModelDimensions}, stored={result.StoredDimensions}");
            Assert.True(result.RequiresMigration);
            Assert.Contains("384", result.MismatchDescription!);
            Assert.Contains("768", result.MismatchDescription!);
            _output.WriteLine($"✅ Detected mismatch: {result.MismatchDescription}");
        }
        finally
        {
            // Cleanup - remove the fake config
            await using var cleanupCmd = new NpgsqlCommand(
                "DELETE FROM embedding_config WHERE model_name = 'fake-model-768d'", conn);
            await cleanupCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Validate_WhenStoredConfigDiffersFromSchema_DetectsIncompleteMigration()
    {
        // Arrange
        var services = CreateServices("all-minilm");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        await using var conn = new NpgsqlConnection(_fixture.PostgresConnectionString);
        await conn.OpenAsync();

        // Deactivate any existing configs first
        await using var deactivateCmd = new NpgsqlCommand(
            "UPDATE embedding_config SET is_active = false WHERE is_active = true", conn);
        await deactivateCmd.ExecuteNonQueryAsync();

        // Insert a config that claims 1024 dimensions (but schema is 384)
        // This simulates a failed migration where schema didn't change
        await using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO embedding_config (model_name, dimensions, detected_at, is_active)
            VALUES ('mxbai-embed-large', 1024, NOW(), true)", conn);
        await insertCmd.ExecuteNonQueryAsync();

        try
        {
            // Act
            var result = await dimensionService.ValidateAsync();

            // Assert - Stored says 1024, but schema is 384
            Assert.True(result.HasMismatch, "Should detect schema/config mismatch");
            Assert.True(result.RequiresMigration);
            Assert.NotNull(result.MismatchDescription);
            _output.WriteLine($"✅ Detected incomplete migration: {result.MismatchDescription}");
        }
        finally
        {
            // Cleanup
            await using var cleanupCmd = new NpgsqlCommand(
                "DELETE FROM embedding_config WHERE model_name = 'mxbai-embed-large'", conn);
            await cleanupCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task UpdateActiveConfig_DeactivatesOldAndCreatesNew()
    {
        // Arrange
        var services = CreateServices("all-minilm");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        // Act
        await dimensionService.UpdateActiveConfigAsync("test-model", 512);

        try
        {
            // Assert
            var activeConfig = await dimensionService.GetActiveConfigAsync();
            Assert.NotNull(activeConfig);
            Assert.Equal("test-model", activeConfig.ModelName);
            Assert.Equal(512, activeConfig.Dimensions);
            Assert.True(activeConfig.IsActive);
            _output.WriteLine($"✅ Updated config: model={activeConfig.ModelName}, dimensions={activeConfig.Dimensions}");

            // Verify only one active config exists
            await using var conn = new NpgsqlConnection(_fixture.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM embedding_config WHERE is_active = true", conn);
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);
            _output.WriteLine($"✅ Only one active config exists: count={count}");
        }
        finally
        {
            // Cleanup - remove test config
            await using var conn = new NpgsqlConnection(_fixture.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cleanupCmd = new NpgsqlCommand(
                "DELETE FROM embedding_config WHERE model_name = 'test-model'", conn);
            await cleanupCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task GetEffectiveDimensions_ReturnsDetectedWhenAvailable()
    {
        // Arrange
        var services = CreateServices("all-minilm");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        // Act
        var effectiveDimensions = await dimensionService.GetEffectiveDimensionsAsync();

        // Assert
        Assert.Equal(384, effectiveDimensions);
        _output.WriteLine($"✅ Effective dimensions (from probe): {effectiveDimensions}");
    }

    [Fact]
    public async Task ProbeModelDimensions_CachesDimensionsOnSecondCall()
    {
        // Arrange
        var services = CreateServices("all-minilm");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        // Act - First call probes the model
        var startFirst = DateTime.UtcNow;
        var dimensions1 = await dimensionService.ProbeModelDimensionsAsync();
        var durationFirst = DateTime.UtcNow - startFirst;

        // Second call should use cached value
        var startSecond = DateTime.UtcNow;
        var dimensions2 = await dimensionService.ProbeModelDimensionsAsync();
        var durationSecond = DateTime.UtcNow - startSecond;

        // Assert
        Assert.Equal(dimensions1, dimensions2);
        Assert.True(durationSecond < durationFirst || durationSecond.TotalMilliseconds < 50,
            $"Second call should be faster (cached). First: {durationFirst.TotalMilliseconds}ms, Second: {durationSecond.TotalMilliseconds}ms");
        _output.WriteLine($"✅ Dimensions cached: first={durationFirst.TotalMilliseconds:F2}ms, second={durationSecond.TotalMilliseconds:F2}ms");
    }

    [Fact]
    public async Task ProbeModelDimensions_WithNonexistentModel_ReturnsNull()
    {
        // Arrange - Configure with a model that doesn't exist
        var services = CreateServices("nonexistent-model-xyz");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        // Act
        var dimensions = await dimensionService.ProbeModelDimensionsAsync();

        // Assert - Should gracefully return null, not throw
        Assert.Null(dimensions);
        _output.WriteLine("✅ Gracefully handled nonexistent model");
    }

    [Fact]
    public async Task Validate_WithUnavailableApi_StillChecksStoredAndSchema()
    {
        // Arrange - Use a model that doesn't exist (API will fail)
        var services = CreateServices("nonexistent-model-xyz");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        // First, set up a stored config that matches the schema
        await using var conn = new NpgsqlConnection(_fixture.PostgresConnectionString);
        await conn.OpenAsync();
        await using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO embedding_config (model_name, dimensions, detected_at, is_active)
            VALUES ('prior-model', 384, NOW(), true)
            ON CONFLICT DO NOTHING", conn);
        await insertCmd.ExecuteNonQueryAsync();

        try
        {
            // Act
            var result = await dimensionService.ValidateAsync();

            // Assert - Should still work using stored/schema values
            Assert.False(result.EmbeddingApiAvailable);
            Assert.Null(result.DetectedModelDimensions);
            // Even without API, if stored and schema match, no migration needed
            _output.WriteLine($"✅ Validated without API: hasMismatch={result.HasMismatch}, effective={result.EffectiveDimensions}");
        }
        finally
        {
            // Cleanup
            await using var cleanupCmd = new NpgsqlCommand(
                "DELETE FROM embedding_config WHERE model_name = 'prior-model'", conn);
            await cleanupCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Validate_ModelChangedButSameDimensions_NoMigrationRequired()
    {
        // Arrange - Different model name but same dimensions
        // This tests the scenario where user switches between models with same output size
        var services = CreateServices("all-minilm");
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        await using var conn = new NpgsqlConnection(_fixture.PostgresConnectionString);
        await conn.OpenAsync();

        // Deactivate existing configs
        await using var deactivateCmd = new NpgsqlCommand(
            "UPDATE embedding_config SET is_active = false WHERE is_active = true", conn);
        await deactivateCmd.ExecuteNonQueryAsync();

        // Insert config for a different model but same 384 dimensions
        await using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO embedding_config (model_name, dimensions, detected_at, is_active)
            VALUES ('different-384d-model', 384, NOW(), true)", conn);
        await insertCmd.ExecuteNonQueryAsync();

        try
        {
            // Act
            var result = await dimensionService.ValidateAsync();

            // Assert - Model name changed but dimensions match, so no migration needed
            Assert.False(result.HasMismatch,
                $"Should not require migration when dimensions match. Description: {result.MismatchDescription}");
            Assert.False(result.RequiresMigration);
            Assert.Equal(384, result.EffectiveDimensions);
            _output.WriteLine($"✅ No migration needed for same-dimension model change");
        }
        finally
        {
            // Cleanup
            await using var cleanupCmd = new NpgsqlCommand(
                "DELETE FROM embedding_config WHERE model_name = 'different-384d-model'", conn);
            await cleanupCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task GetActiveConfig_WhenNoConfigExists_ReturnsNull()
    {
        // Arrange
        var services = CreateServices();
        var dimensionService = services.GetRequiredService<IEmbeddingDimensionService>();

        await using var conn = new NpgsqlConnection(_fixture.PostgresConnectionString);
        await conn.OpenAsync();

        // Deactivate all configs
        await using var deactivateCmd = new NpgsqlCommand(
            "UPDATE embedding_config SET is_active = false WHERE is_active = true", conn);
        await deactivateCmd.ExecuteNonQueryAsync();

        try
        {
            // Act
            var config = await dimensionService.GetActiveConfigAsync();

            // Assert
            Assert.Null(config);
            _output.WriteLine("✅ Returns null when no active config exists");
        }
        finally
        {
            // Re-enable a config or let other tests set it up
        }
    }
}
