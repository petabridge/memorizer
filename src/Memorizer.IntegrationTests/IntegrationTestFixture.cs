using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Memorizer.Services;
using Testcontainers.PostgreSql;

namespace Memorizer.IntegrationTests;

[CollectionDefinition(nameof(IntegrationTestCollection))]
public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture> { }

public class IntegrationTestFixture : IAsyncLifetime
{
    public readonly PostgreSqlContainer PostgresContainer;
    public readonly IContainer OllamaContainer;
    
    public string PostgresConnectionString => PostgresContainer.GetConnectionString();
    public string OllamaApiUrl => $"http://{OllamaContainer.Hostname}:{OllamaContainer.GetMappedPublicPort(11434)}";

    public IntegrationTestFixture()
    {
        PostgresContainer = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg17")
            .WithCleanUp(true)
            .WithPortBinding(5432, true)
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithDatabase("postgmem")
            .Build();

        OllamaContainer = new ContainerBuilder()
            .WithImage("ollama/ollama:latest")
            .WithPortBinding(11434, true)
            .WithCleanUp(true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(11434))
            .Build();
    }

    public async Task InitializeAsync()
    {
        // Start containers
        await Task.WhenAll(
            PostgresContainer.StartAsync(),
            OllamaContainer.StartAsync()
        );

        // Run migrations
        await SchemaMigrator.MigrateAsync(PostgresConnectionString);

        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri(OllamaApiUrl);
        httpClient.Timeout = TimeSpan.FromMinutes(5); // Model pull can take a while
        
        // Pull the embedding model
        var embeddingModelPullRequest = new
        {
            name = "all-minilm"
        };
        
        var embeddingResponse = await httpClient.PostAsJsonAsync("/api/pull", embeddingModelPullRequest);
        embeddingResponse.EnsureSuccessStatusCode();

        // Wait for the embedding model to be ready
        var embeddingRequest = new
        {
            model = "all-minilm",
            prompt = "test"
        };

        var ready = false;
        var attempts = 0;
        const int maxAttempts = 10;

        while (!ready && attempts < maxAttempts)
        {
            try
            {
                var embeddingTestResponse = await httpClient.PostAsJsonAsync("/api/embeddings", embeddingRequest);
                if (embeddingTestResponse.IsSuccessStatusCode)
                {
                    ready = true;
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    attempts++;
                }
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                attempts++;
            }
        }

        if (!ready)
        {
            throw new Exception("Failed to initialize Ollama embedding model after multiple attempts");
        }
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            PostgresContainer.DisposeAsync().AsTask(),
            OllamaContainer.DisposeAsync().AsTask()
        );
    }
} 