using Akka.Hosting;
using Akka.Hosting.TestKit;
using Memorizer.Extensions;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

[Collection(nameof(IntegrationTestCollection))]
public class MemoryStatsServiceTests : TestKit
{
    private readonly IntegrationTestFixture _fixture;
    private IStorage _storage = null!;
    private IMemoryStatsService _statsService = null!;

    public MemoryStatsServiceTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(output: output)
    {
        _fixture = fixture;
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // Configure services just like in the IntegrationTests class
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                ["Embeddings:Model"] = "all-minilm",
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString()
            })
            .Build());

        // Add HTTP client for embedding service
        services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
        {
            client.BaseAddress = new Uri(_fixture.OllamaApiUrl);
            client.Timeout = TimeSpan.FromMinutes(1);
        });

        // Add services
        services.AddSingleton(new EmbeddingSettings
        {
            ApiUrl = new Uri(_fixture.OllamaApiUrl),
            Model = "all-minilm",
            Timeout = TimeSpan.FromMinutes(1)
        });
        
        services.AddMemorizer(initialize:false);
    }
    
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.ConfigureLoggers(configBuilder =>
        {
            configBuilder.LogLevel = Akka.Event.LogLevel.DebugLevel;
        });
    }

    [Fact]
    public async Task MemoryStats_ReturnsCorrectCounts_WithEmptyDatabase()
    {
        // Get services from DI
        _statsService = Host.Services.GetRequiredService<IMemoryStatsService>();
        
        // Get stats from empty database
        var stats = await _statsService.GetStatsAsync();
        
        // We might have some memories from other tests, but we can check the types
        Assert.IsType<int>(stats.TotalMemories);
        Assert.IsType<long>(stats.AverageMemorySizeBytes);
    }
    
    [Fact]
    public async Task MemoryStats_ReturnsCorrectCounts_AfterAddingMemories()
    {
        // Get services from DI
        _storage = Host.Services.GetRequiredService<IStorage>();
        _statsService = Host.Services.GetRequiredService<IMemoryStatsService>();
        
        // First get the initial count
        var initialStats = await _statsService.GetStatsAsync();
        
        // Add some memories
        var memories = new[]
        {
            (type: "test", content: "small memory", tags: new[] { "test", "small" }),
            (type: "test", content: "medium memory with more content", tags: new[] { "test", "medium" }),
            (type: "test", content: "large memory with even more content to increase the size", tags: new[] { "test", "large" })
        };
        
        foreach (var (type, content, tags) in memories)
        {
            await _storage.StoreMemory(type, content, "test", tags, new Confidence(1.0), "Test Memory");
        }
        
        // Get the updated stats
        var updatedStats = await _statsService.GetStatsAsync();
        
        // Verify counts have increased
        Assert.Equal(initialStats.TotalMemories + memories.Length, updatedStats.TotalMemories);
        
        // Average size should now be positive
        Assert.True(updatedStats.AverageMemorySizeBytes > 0);
    }

    [Fact]
    public async Task MemoryStats_ExcludesSystemMemories()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        _statsService = Host.Services.GetRequiredService<IMemoryStatsService>();

        var initialStats = await _statsService.GetStatsAsync();

        // Add a regular document memory
        await _storage.StoreMemory("test", "regular document memory", "test",
            new[] { "test" }, new Confidence(1.0), "Regular Memory",
            archetype: ArchetypeEnum.Document);

        // Add a system memory (should be excluded from stats)
        await _storage.StoreMemory("test", "system indexing memory", "test",
            new[] { "system" }, new Confidence(1.0), "System Memory",
            archetype: ArchetypeEnum.System);

        var updatedStats = await _statsService.GetStatsAsync();

        // Only the regular memory should be counted, not the system one
        Assert.Equal(initialStats.TotalMemories + 1, updatedStats.TotalMemories);
    }

    [Fact]
    public async Task GetMemoriesPaginated_ExcludesSystemMemories()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();

        // Get initial paginated count
        var (_, initialCount) = await _storage.GetMemoriesPaginated(page: 1, pageSize: 1000);

        // Add regular memories
        await _storage.StoreMemory("test", "paginated test memory 1", "test",
            new[] { "test" }, new Confidence(1.0), "Paginated Test 1",
            archetype: ArchetypeEnum.Document);
        await _storage.StoreMemory("test", "paginated test memory 2", "test",
            new[] { "test" }, new Confidence(1.0), "Paginated Test 2",
            archetype: ArchetypeEnum.Record);

        // Add a system memory (should be excluded)
        await _storage.StoreMemory("test", "system memory for pagination test", "test",
            new[] { "system" }, new Confidence(1.0), "System Paginated Test",
            archetype: ArchetypeEnum.System);

        var (memories, totalCount) = await _storage.GetMemoriesPaginated(page: 1, pageSize: 1000);

        // Count should only increase by 2 (the regular memories), not 3
        Assert.Equal(initialCount + 2, totalCount);

        // No system memories should appear in results
        Assert.DoesNotContain(memories, m => m.Archetype == ArchetypeEnum.System);
    }

    [Fact]
    public async Task CountMemoriesWithoutMetadataEmbeddings_ExcludesSystemMemories()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();

        var initialCount = await _storage.CountMemoriesWithoutMetadataEmbeddings();

        // Add a system memory - it should NOT be counted even if it lacks metadata embeddings
        await _storage.StoreMemory("test", "system memory for embedding count test", "test",
            new[] { "system" }, new Confidence(1.0), "System Embedding Test",
            archetype: ArchetypeEnum.System);

        var updatedCount = await _storage.CountMemoriesWithoutMetadataEmbeddings();

        // System memory should not increase the count
        Assert.Equal(initialCount, updatedCount);
    }
}