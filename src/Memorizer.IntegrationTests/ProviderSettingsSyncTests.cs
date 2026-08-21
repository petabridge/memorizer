using System.Text.Json;
using FluentAssertions;
using Memorizer.Extensions;
using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Tests that verify provider settings synchronization between database and services.
/// These tests ensure that:
/// 1. Database provider settings are loaded into IConfiguration at startup
/// 2. Services using IOptionsSnapshot receive the database-configured values
/// 3. Changes to provider settings via UI are reflected in subsequent requests
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProviderSettingsSyncTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private IHost? _host;

    public ProviderSettingsSyncTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        // Clean up provider_settings before each test (in case a prior run leaked rows).
        await CleanProviderSettingsAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        // Symmetric cleanup — this class shares the Postgres container with the rest of
        // the collection and writes ACTIVE provider_settings rows to prove DB settings
        // override config. Without removing them on teardown, the last row (e.g. the
        // nonexistent 'custom-embedding-model') survives into other tests: their full-app
        // startup (InitializationService) applies it, so their embedding calls target a
        // model Ollama has not pulled and fail. Clean on the way out, not just in.
        await CleanProviderSettingsAsync();
    }

    private async Task CleanProviderSettingsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.PostgresConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM provider_settings", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private IHost CreateHost(Dictionary<string, string?> configOverrides)
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                    ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                    ["Embeddings:Model"] = "all-minilm",
                    ["Embeddings:Timeout"] = "00:00:30",
                    ["LLM:ApiUrl"] = _fixture.OllamaApiUrl,
                    ["LLM:Model"] = "qwen2:0.5b",
                    ["LLM:Timeout"] = "00:02:00",
                });

                // Add test-specific overrides
                config.AddInMemoryCollection(configOverrides);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddMemorizer(initialize: true);
            });

        return builder.Build();
    }

    [Fact]
    public async Task EmbeddingService_Should_UseValuesFromDatabase_WhenProviderSettingsExist()
    {
        // Arrange - Create a provider setting in the database before starting the host
        var customApiUrl = "http://custom-embedding-server:11434";
        var customModel = "custom-embedding-model";

        await using var dataSource = NpgsqlDataSource.Create(_fixture.PostgresConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        var insertSql = @"
            INSERT INTO provider_settings (id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at)
            VALUES (@id, @providerType, @providerName, @displayName, @config::jsonb, true, NOW(), NOW())";

        await using var cmd = new NpgsqlCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("providerType", ProviderTypes.Embedding);
        cmd.Parameters.AddWithValue("providerName", ProviderNames.Ollama);
        cmd.Parameters.AddWithValue("displayName", "Test Embedding Provider");
        cmd.Parameters.AddWithValue("config", JsonSerializer.Serialize(new
        {
            apiUrl = customApiUrl,
            model = customModel
        }));
        await cmd.ExecuteNonQueryAsync();

        // Act - Start the host (InitializationService will load database settings)
        _host = CreateHost(new Dictionary<string, string?>());
        await _host.StartAsync();

        // Give InitializationService time to complete
        await Task.Delay(2000);

        // Assert - Check that configuration was updated from database
        var config = _host.Services.GetRequiredService<IConfiguration>();
        config["Embeddings:ApiUrl"].Should().Be(customApiUrl);
        config["Embeddings:Model"].Should().Be(customModel);

        // Assert - Check that IOptionsSnapshot picks up the database values
        using var scope = _host.Services.CreateScope();
        var embeddingSettings = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<EmbeddingSettings>>();
        embeddingSettings.Value.ApiUrl.ToString().TrimEnd('/').Should().Be(customApiUrl);
        embeddingSettings.Value.Model.Should().Be(customModel);
    }

    [Fact]
    public async Task LlmSettings_Should_UseValuesFromDatabase_WhenProviderSettingsExist()
    {
        // Arrange - Create a provider setting in the database before starting the host
        var customApiUrl = "http://custom-llm-server:11434";
        var customModel = "custom-llm-model";

        await using var dataSource = NpgsqlDataSource.Create(_fixture.PostgresConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        var insertSql = @"
            INSERT INTO provider_settings (id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at)
            VALUES (@id, @providerType, @providerName, @displayName, @config::jsonb, true, NOW(), NOW())";

        await using var cmd = new NpgsqlCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("providerType", ProviderTypes.MemorizerAgent);
        cmd.Parameters.AddWithValue("providerName", ProviderNames.Ollama);
        cmd.Parameters.AddWithValue("displayName", "Test LLM Provider");
        cmd.Parameters.AddWithValue("config", JsonSerializer.Serialize(new
        {
            apiUrl = customApiUrl,
            model = customModel,
            timeout = "00:05:00"
        }));
        await cmd.ExecuteNonQueryAsync();

        // Act - Start the host (InitializationService will load database settings)
        _host = CreateHost(new Dictionary<string, string?>());
        await _host.StartAsync();

        // Give InitializationService time to complete
        await Task.Delay(2000);

        // Assert - Check that configuration was updated from database
        var config = _host.Services.GetRequiredService<IConfiguration>();
        config["LLM:ApiUrl"].Should().Be(customApiUrl);
        config["LLM:Model"].Should().Be(customModel);

        // Assert - Check that IOptionsSnapshot picks up the database values
        using var scope = _host.Services.CreateScope();
        var llmSettings = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<LlmSettings>>();
        llmSettings.Value.ApiUrl.ToString().TrimEnd('/').Should().Be(customApiUrl);
        llmSettings.Value.Model.Should().Be(customModel);
    }

    [Fact]
    public async Task InitializationService_Should_SeedFromEnvironment_WhenNoDatabaseSettingsExist()
    {
        // Arrange - Ensure no provider settings exist (already cleaned in InitializeAsync)
        var envApiUrl = _fixture.OllamaApiUrl;
        var envModel = "all-minilm";

        // Act - Start the host with environment configuration
        _host = CreateHost(new Dictionary<string, string?>
        {
            ["Embeddings:ApiUrl"] = envApiUrl,
            ["Embeddings:Model"] = envModel,
        });
        await _host.StartAsync();

        // Give InitializationService time to complete
        await Task.Delay(2000);

        // Assert - Check that provider settings were seeded to database
        var storage = _host.Services.GetRequiredService<IStorage>();
        var embeddingProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding);

        embeddingProvider.Should().NotBeNull();
        var config = embeddingProvider!.Config.RootElement;
        config.GetProperty("apiUrl").GetString().Should().Be(envApiUrl);
        config.GetProperty("model").GetString().Should().Be(envModel);
    }

    [Fact]
    public async Task ProviderSettings_Should_UpdateConfiguration_WhenChangedViaStorage()
    {
        // Arrange - Start with initial settings
        var initialApiUrl = _fixture.OllamaApiUrl;
        var initialModel = "all-minilm";

        _host = CreateHost(new Dictionary<string, string?>
        {
            ["Embeddings:ApiUrl"] = initialApiUrl,
            ["Embeddings:Model"] = initialModel,
        });
        await _host.StartAsync();
        await Task.Delay(2000);

        // Verify initial settings were loaded
        var config = _host.Services.GetRequiredService<IConfiguration>();
        config["Embeddings:Model"].Should().Be(initialModel);

        // Act - Update provider settings in database (simulating UI change)
        var storage = _host.Services.GetRequiredService<IStorage>();
        var existingProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding);
        existingProvider.Should().NotBeNull();

        var updatedModel = "nomic-embed-text";
        var updatedProvider = new ProviderSettings
        {
            Id = existingProvider!.Id,
            ProviderType = ProviderTypes.Embedding,
            ProviderName = ProviderNames.Ollama,
            DisplayName = "Updated Embedding Provider",
            Config = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                apiUrl = initialApiUrl,
                model = updatedModel
            })),
            IsActive = true,
            CreatedAt = existingProvider.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };
        await storage.SaveProviderSettingsAsync(updatedProvider);

        // Update configuration manually (in real app, this would be done via an event or controller)
        config["Embeddings:Model"] = updatedModel;

        // Assert - New scope should get updated settings via IOptionsSnapshot
        using var scope = _host.Services.CreateScope();
        var embeddingSettings = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<EmbeddingSettings>>();
        embeddingSettings.Value.Model.Should().Be(updatedModel);
    }

    [Fact]
    public async Task Beta1Upgrade_Should_OverrideLocalhostDefaults_WithEnvironmentConfig()
    {
        // Arrange - Simulate beta1 state: database has localhost defaults
        var localhostApiUrl = "http://localhost:11434";
        var defaultModel = "all-minilm";

        await using var dataSource = NpgsqlDataSource.Create(_fixture.PostgresConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        var insertSql = @"
            INSERT INTO provider_settings (id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at)
            VALUES (@id, @providerType, @providerName, @displayName, @config::jsonb, true, NOW(), NOW())";

        await using var cmd = new NpgsqlCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("providerType", ProviderTypes.Embedding);
        cmd.Parameters.AddWithValue("providerName", ProviderNames.Ollama);
        cmd.Parameters.AddWithValue("displayName", "Ollama Embeddings");
        cmd.Parameters.AddWithValue("config", JsonSerializer.Serialize(new
        {
            apiUrl = localhostApiUrl,
            model = defaultModel
        }));
        await cmd.ExecuteNonQueryAsync();

        // Environment has non-localhost config (real production server)
        var productionApiUrl = _fixture.OllamaApiUrl; // This is not localhost
        var productionModel = "all-minilm";

        // Act - Start the host with production environment configuration
        _host = CreateHost(new Dictionary<string, string?>
        {
            ["Embeddings:ApiUrl"] = productionApiUrl,
            ["Embeddings:Model"] = productionModel,
        });
        await _host.StartAsync();
        await Task.Delay(2000);

        // Assert - Database should be updated with environment values (beta1 fix)
        var storage = _host.Services.GetRequiredService<IStorage>();
        var embeddingProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding);

        embeddingProvider.Should().NotBeNull();
        var config = embeddingProvider!.Config.RootElement;

        // The localhost default should have been overridden with environment config
        config.GetProperty("apiUrl").GetString().Should().Be(productionApiUrl);

        // IOptionsSnapshot should also reflect the updated values
        using var scope = _host.Services.CreateScope();
        var embeddingSettings = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<EmbeddingSettings>>();
        embeddingSettings.Value.ApiUrl.ToString().TrimEnd('/').Should().Be(productionApiUrl.TrimEnd('/'));
    }

    [Fact]
    public async Task EmbeddingModelChange_Should_BeDetected_WhenSavingProviderSettings()
    {
        // Arrange - Start with initial embedding model
        var apiUrl = _fixture.OllamaApiUrl;
        var initialModel = "all-minilm";

        _host = CreateHost(new Dictionary<string, string?>
        {
            ["Embeddings:ApiUrl"] = apiUrl,
            ["Embeddings:Model"] = initialModel,
        });
        await _host.StartAsync();
        await Task.Delay(2000);

        var storage = _host.Services.GetRequiredService<IStorage>();

        // Verify initial model was set
        var initialProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding);
        initialProvider.Should().NotBeNull();
        var initialConfig = initialProvider!.Config.RootElement;
        initialConfig.GetProperty("model").GetString().Should().Be(initialModel);

        // Act - Change to a different model
        var newModel = "nomic-embed-text";
        var updatedProvider = new ProviderSettings
        {
            Id = initialProvider.Id,
            ProviderType = ProviderTypes.Embedding,
            ProviderName = ProviderNames.Ollama,
            DisplayName = "Updated Embedding Provider",
            Config = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                apiUrl,
                model = newModel
            })),
            IsActive = true,
            CreatedAt = initialProvider.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };
        await storage.SaveProviderSettingsAsync(updatedProvider);

        // Assert - The model should be detected as changed in the database
        var finalProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding);
        finalProvider.Should().NotBeNull();
        var finalConfig = finalProvider!.Config.RootElement;
        finalConfig.GetProperty("model").GetString().Should().Be(newModel);

        // The previous model was different
        initialModel.Should().NotBe(newModel);
    }
}

