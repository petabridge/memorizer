using Memorizer.Extensions;
using Memorizer.Models;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for the hybrid search behavior that the web UI now uses by
/// default (see ADR 2026-02-14-hybrid-search-rrf.md): short keyword queries that
/// fail with metadata-embedding vector search at the default threshold are still
/// found via the full-text leg.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class HybridSearchIntegrationTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly IServiceProvider _services;

    public void Dispose()
    {
        (_services as IDisposable)?.Dispose();
    }

    public HybridSearchIntegrationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
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
    public async Task HybridSearch_FindsShortKeywordQueries_ThatVectorSearchMisses()
    {
        // Arrange - the ADR scenario: short keyword queries fail at the default threshold
        var storage = _services.GetRequiredService<IStorage>();

        var created = new List<MemoryId>();
        try
        {
            created.Add((await storage.StoreMemory(
                "reference", "Notes about race conditions in concurrent code and mutex handling", "test",
                new[] { "concurrency" }, new Confidence(1.0), "Race condition deep dive")).Id);
            created.Add((await storage.StoreMemory(
                "reference", "Understanding dependency injection containers and service lifetimes", "test",
                new[] { "di" }, new Confidence(1.0), "Dependency injection")).Id);

            // Act - hybrid search should surface exact keyword matches via the FTS leg
            var hybridResults = await storage.HybridSearch("race condition", limit: 5, minSimilarity: null, filterTags: null);

            // Assert
            Assert.NotEmpty(hybridResults);
            Assert.Contains(hybridResults, m => m.Title?.Contains("Race condition") == true);
        }
        finally
        {
            foreach (var id in created)
                await storage.Delete(id);
        }
    }

    [Fact]
    public async Task VectorSearch_StillReturnsResults_ForShortKeywordQueries()
    {
        // Arrange
        var storage = _services.GetRequiredService<IStorage>();

        var created = new List<MemoryId>();
        try
        {
            created.Add((await storage.StoreMemory(
                "reference", "Notes about race conditions in concurrent code and mutex handling", "test",
                new[] { "concurrency" }, new Confidence(1.0), "Race condition deep dive")).Id);

            // Act - method=vector mode must still work (metadata embedding search)
            var vectorResults = await storage.SearchWithMetadataEmbedding(
                "race condition", limit: 5, new SimilarityScore(0.3), filterTags: null);

            // Assert - a lenient threshold should still surface the result
            Assert.Contains(vectorResults, m => m.Title?.Contains("Race condition") == true);
        }
        finally
        {
            foreach (var id in created)
                await storage.Delete(id);
        }
    }
}
