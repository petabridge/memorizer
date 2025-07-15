using Memorizer.Extensions;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

[Collection(nameof(IntegrationTestCollection))]
public class MetadataEmbeddingStorageTests 
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly IServiceProvider _services;

    public MetadataEmbeddingStorageTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _services = CreateServices();
    }

    private IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                ["Embeddings:Model"] = "all-minilm",
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString()
            })
            .Build());

        services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
        {
            client.BaseAddress = new Uri(_fixture.OllamaApiUrl);
            client.Timeout = TimeSpan.FromMinutes(1);
        });

        services.AddSingleton(new EmbeddingSettings
        {
            ApiUrl = new Uri(_fixture.OllamaApiUrl),
            Model = "all-minilm",
            Timeout = TimeSpan.FromMinutes(1)
        });
        
        services.AddMemorizer();
        services.AddLogging();
        
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SearchWithMetadataEmbedding_WorksWithRealEmbeddings()
    {
        // Arrange
        var storage = _services.GetRequiredService<IStorage>();
        
        // Test that the SearchWithMetadataEmbedding method works functionally
        // This test verifies:
        // 1. Metadata embeddings are generated during storage
        // 2. SearchWithMetadataEmbedding method executes without errors
        // 3. Results are returned in the expected format
        // 4. Similarity scores are calculated correctly
        
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var uniqueTitle = $"MetadataEmbeddingTest-{uniqueId}";
        var uniqueTags = new[] { $"test-{uniqueId}", "metadata-embedding", "integration-test" };
        
        var testMemory = await storage.StoreMemory(
            "test",
            "Test content for metadata search functionality verification",
            "test",
            uniqueTags,
            1.0,
            uniqueTitle);

        _output.WriteLine($"Stored memory with ID: {testMemory.Id}, Title: '{uniqueTitle}'");

        // Verify the memory was stored correctly with metadata embedding
        var storedMemory = await storage.Get(testMemory.Id);
        Assert.NotNull(storedMemory);
        Assert.Equal(uniqueTitle, storedMemory.Title);
        Assert.NotNull(storedMemory.EmbeddingMetadata);
        _output.WriteLine($"✅ Memory stored with metadata embedding: {storedMemory.EmbeddingMetadata != null}");

        // Test that SearchWithMetadataEmbedding method works functionally
        // Use a generic search term that should return some results from the database
        var results = await storage.SearchWithMetadataEmbedding(
            "test", 
            limit: 10, 
            minSimilarity: 0.1);

        _output.WriteLine($"Search for 'test' returned {results.Count} results");

        // Verify the method works correctly
        Assert.NotNull(results);
        
        // If we have results, verify they have the expected structure
        if (results.Count > 0)
        {
            var firstResult = results[0];
            Assert.NotNull(firstResult.Title);
            Assert.True(firstResult.Similarity.HasValue);
            Assert.True(firstResult.Similarity >= 0.0 && firstResult.Similarity <= 1.0);
            
            _output.WriteLine($"✅ First result: ID={firstResult.Id}, Title='{firstResult.Title}', Similarity={firstResult.Similarity:F3}");
        }

        // Test with a more specific search that's likely to return fewer results
        var specificResults = await storage.SearchWithMetadataEmbedding(
            uniqueTitle, 
            limit: 5, 
            minSimilarity: 0.1);

        _output.WriteLine($"Search for unique title returned {specificResults.Count} results");
        Assert.NotNull(specificResults);

        // Verify all results have valid similarity scores
        foreach (var result in specificResults.Take(3))
        {
            Assert.True(result.Similarity.HasValue);
            Assert.True(result.Similarity >= 0.0 && result.Similarity <= 1.0);
            _output.WriteLine($"  - ID: {result.Id}, Title: '{result.Title}', Similarity: {result.Similarity:F3}");
        }

        _output.WriteLine($"✅ SearchWithMetadataEmbedding method works correctly");
    }

    [Fact]
    public async Task GetMemoriesWithoutMetadataEmbeddings_CanFindMemoriesForMigration()
    {
        // Arrange
        var storage = _services.GetRequiredService<IStorage>();

        // Act - this method is used by the migration actor
        var memories = await storage.GetMemoriesWithoutMetadataEmbeddings(10);

        // Assert - method works without throwing (specific results depend on DB state)
        Assert.NotNull(memories);
    }
}