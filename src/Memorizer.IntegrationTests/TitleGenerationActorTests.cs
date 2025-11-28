using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Memorizer.Actors;
using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pgvector;
using Xunit.Abstractions;
using SimilarMemory = Memorizer.Models.SimilarMemory;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Simple tests to verify TitleGenerationActor ActorSelection fixes work
/// </summary>
public class TitleGenerationActorTests : TestKit
{
    private readonly ITestOutputHelper _output;

    public TitleGenerationActorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // Add minimal required services for testing
        services.AddSingleton(new LlmSettings
        {
            ApiUrl = new Uri("http://localhost:11434"),
            Model = "test-model",
            Timeout = TimeSpan.FromMinutes(1)
        });
        
        // Add mock services (minimal implementations)
        services.AddSingleton<IStorage, MockStorage>();
        services.AddSingleton<ILlmService, MockLlmService>();
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithActors((system, registry, resolver) =>
        {
            // Register TitleGenerationActor with dependency injection - this is what we're testing!
            var titleGenerationActorProps = resolver.Props<TitleGenerationActor>();
            var titleGenerationActor = system.ActorOf(titleGenerationActorProps, "title-generation");
            registry.Register<TitleGenerationActorKey>(titleGenerationActor);
        });
    }

    [Fact]
    public void TitleGenerationActor_ShouldBeRegistered_InActorRegistry()
    {
        // Act - This is the core test of our ActorSelection fix
        var titleGenerationActor = ActorRegistry.Get<TitleGenerationActorKey>();
        
        // Assert
        Assert.NotNull(titleGenerationActor);
        Assert.Equal("title-generation", titleGenerationActor.Path.Name);
        
        _output.WriteLine("✅ TitleGenerationActor successfully registered in ActorRegistry via IRequiredActor pattern");
    }

    [Fact]
    public void ActorRegistry_ShouldResolveActor_WithCorrectType()
    {
        // Act
        var titleGenerationActor = ActorRegistry.Get<TitleGenerationActorKey>();
        
        // Assert - Verify the actor path and system are correct
        Assert.NotNull(titleGenerationActor);
        Assert.Contains("title-generation", titleGenerationActor.Path.ToString());
        
        _output.WriteLine($"✅ Actor resolved correctly: {titleGenerationActor.Path}");
    }

    [Fact]
    public async Task TitleGenerationActor_ProgressStream_ShouldCompleteGracefully()
    {
        // Arrange
        var mockStorage = new MockStorageWithMemories();
        var mockLlm = new MockLlmService();
        var settings = new LlmSettings
        {
            ApiUrl = new Uri("http://localhost:11434"),
            Model = "test-model",
            Timeout = TimeSpan.FromMinutes(1)
        };

        // Create a new actor for this test
        var testActor = Sys.ActorOf(TitleGenerationActor.Props(mockStorage, mockLlm, settings), "title-generation-test");

        // Act - Subscribe BEFORE starting the batch (should get Idle status and complete immediately)
        var idleSubscription = await testActor.Ask<ProgressSubscription>(
            new SubscribeToProgress("idle-subscriber"),
            TimeSpan.FromSeconds(5));

        // Collect idle subscription events (should receive Idle status and complete immediately)
        var idleEvents = new List<ProgressEvent>();
        await foreach (var evt in idleSubscription.Reader.ReadAllAsync())
        {
            idleEvents.Add(evt);
        }

        Assert.Single(idleEvents);
        Assert.Equal(JobStatus.Idle, idleEvents[0].Status);
        _output.WriteLine("✅ Idle subscription completed immediately with Idle status when no batch is running");

        // Start the batch FIRST (Tell queues the message), then Subscribe
        // Since actor processes messages in order: Batch → Running state → Subscribe → get running subscription
        testActor.Tell(new GenerateTitlesForUntitled { BatchSize = 3, RequestedBy = "test" });

        // Subscribe - this Ask is queued after the batch Tell, so the actor will be in Running state
        var activeSubscription = await testActor.Ask<ProgressSubscription>(
            new SubscribeToProgress("active-subscriber"),
            TimeSpan.FromSeconds(5));

        // Collect events from the running batch (even if they all come at once)
        var progressEventsList = new List<ProgressEvent>();
        await foreach (var evt in activeSubscription.Reader.ReadAllAsync())
        {
            progressEventsList.Add(evt);
            _output.WriteLine($"Progress: {evt.TotalProcessed}/{evt.TotalItems} processed, Status={evt.Status}");
        }

        // Assert
        Assert.NotEmpty(progressEventsList);
        _output.WriteLine($"✅ Progress stream emitted {progressEventsList.Count} events");

        // Verify we got a completion event
        var lastEvent = progressEventsList.Last();
        Assert.True(lastEvent.IsCompleted, "Last event should be a completion status");
        Assert.Equal(3, lastEvent.TotalProcessed);
        Assert.Equal(3, lastEvent.TotalSuccessful);
        _output.WriteLine("✅ Progress stream completed gracefully after batch finished");
    }

    /// <summary>
    /// Mock storage that returns a small list of untitled memories for testing
    /// </summary>
    private class MockStorageWithMemories : IStorage
    {
        public Task<List<Memory>> GetMemoriesWithoutTitles(int limit, CancellationToken cancellationToken = default)
        {
            var memories = new List<Memory>
            {
                new() { Id = Guid.NewGuid(), Text = "Test memory 1", Type = "test", Tags = [] },
                new() { Id = Guid.NewGuid(), Text = "Test memory 2", Type = "test", Tags = [] },
                new() { Id = Guid.NewGuid(), Text = "Test memory 3", Type = "test", Tags = [] }
            };
            return Task.FromResult(memories);
        }

        public Task UpdateMemoryTitle(Guid memoryId, string title, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<Memory> StoreMemory(string type, string text, string source, string[]? tags = null, double confidence = 1, string? title = null, Guid? relatedToId = null, string? relationshipType = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<Memory?> Get(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> GetMany(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> Search(string query, int limit = 10, double minSimilarity = 0.7, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<(List<Memory> Memories, int TotalCount)> GetMemoriesPaginated(int page, int pageSize, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<Memory?> UpdateMemory(Guid id, string type, string text, string source, string[]? tags = null, double confidence = 1, string? title = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<MemoryRelationship> CreateRelationship(Guid fromId, Guid toId, string relationshipType, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<MemoryRelationship> CreateRelationship(Guid fromId, Guid toId, string relationshipType, double? score, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<MemoryRelationship>> GetRelationships(Guid memoryId, string? relationshipType = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<SimilarMemory>> GetSimilarMemories(Guid memoryId, double minSimilarity = 0.7, int limit = 10, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<SimilarMemory>());

        Task<List<string>> IStorage.GetDistinctMemoryTypes(CancellationToken cancellationToken)
            => Task.FromResult(new List<string> { "test", "mock" });

        public Task<int> CountMemoriesWithoutMetadataEmbeddings(CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<List<Memory>> GetMemoriesWithoutMetadataEmbeddings(int limit, bool includeExisting = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<Memory>());

        public Task UpdateMemoryMetadataEmbedding(Guid memoryId, Vector embedding, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateMemoryEmbeddings(Guid memoryId, Vector contentEmbedding, Vector metadataEmbedding, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<List<Memory>> SearchWithFullEmbedding(string query, int limit = 10, double minSimilarity = 0.7, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> SearchWithMetadataEmbedding(string query, int limit = 10, double minSimilarity = 0.7, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<(List<Memory> FullResults, List<Memory> MetadataResults)> CompareSearchMethods(string query, int limit = 10, double minSimilarity = 0.7, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        // Versioning support
        public Task<List<MemoryEvent>> GetEvents(Guid memoryId, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<MemoryEvent>());

        public Task<List<MemoryVersion>> GetVersionHistory(Guid memoryId, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<MemoryVersion>());

        public Task<MemoryVersion?> GetVersion(Guid memoryId, int versionNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<MemoryVersion?>(null);

        public Task<Memory?> RevertToVersion(Guid memoryId, int versionNumber, string? changedBy = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<int> PurgeVersionsKeepingLatest(Guid memoryId, int versionsToKeep, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<int> PurgeVersionsOlderThan(DateTime cutoffDate, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<VersionStats> GetVersionStats(CancellationToken cancellationToken = default)
            => Task.FromResult(new VersionStats());
    }

    /// <summary>
    /// Minimal mock storage for testing
    /// </summary>
    private class MockStorage : IStorage
    {
        public Task<Memory> StoreMemory(string type, string text, string source, string[]? tags = null, double confidence = 1, string? title = null, Guid? relatedToId = null, string? relationshipType = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<Memory?> Get(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> GetMany(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> Search(string query, int limit = 10, double minSimilarity = 0.7, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<(List<Memory> Memories, int TotalCount)> GetMemoriesPaginated(int page, int pageSize, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<Memory?> UpdateMemory(Guid id, string type, string text, string source, string[]? tags = null, double confidence = 1, string? title = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<MemoryRelationship> CreateRelationship(Guid fromId, Guid toId, string relationshipType, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<MemoryRelationship> CreateRelationship(Guid fromId, Guid toId, string relationshipType, double? score, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<MemoryRelationship>> GetRelationships(Guid memoryId, string? relationshipType = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<SimilarMemory>> GetSimilarMemories(Guid memoryId, double minSimilarity = 0.7, int limit = 10, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<SimilarMemory>());

        public Task<List<Memory>> GetMemoriesWithoutTitles(int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<Memory>());

        public Task UpdateMemoryTitle(Guid memoryId, string title, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        Task<List<string>> IStorage.GetDistinctMemoryTypes(CancellationToken cancellationToken)
            => Task.FromResult(new List<string> { "test", "mock" });

        public Task<int> CountMemoriesWithoutMetadataEmbeddings(CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<List<Memory>> GetMemoriesWithoutMetadataEmbeddings(int limit, bool includeExisting = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<Memory>());

        public Task UpdateMemoryMetadataEmbedding(Guid memoryId, Vector embedding, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateMemoryEmbeddings(Guid memoryId, Vector contentEmbedding, Vector metadataEmbedding, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<List<Memory>> SearchWithFullEmbedding(string query, int limit = 10, double minSimilarity = 0.7, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> SearchWithMetadataEmbedding(string query, int limit = 10, double minSimilarity = 0.7, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<(List<Memory> FullResults, List<Memory> MetadataResults)> CompareSearchMethods(string query, int limit = 10, double minSimilarity = 0.7, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        // Versioning support
        public Task<List<MemoryEvent>> GetEvents(Guid memoryId, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<MemoryEvent>());

        public Task<List<MemoryVersion>> GetVersionHistory(Guid memoryId, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<MemoryVersion>());

        public Task<MemoryVersion?> GetVersion(Guid memoryId, int versionNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<MemoryVersion?>(null);

        public Task<Memory?> RevertToVersion(Guid memoryId, int versionNumber, string? changedBy = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<int> PurgeVersionsKeepingLatest(Guid memoryId, int versionsToKeep, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<int> PurgeVersionsOlderThan(DateTime cutoffDate, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<VersionStats> GetVersionStats(CancellationToken cancellationToken = default)
            => Task.FromResult(new VersionStats());
    }

    /// <summary>
    /// Minimal mock LLM service for testing
    /// </summary>
    private class MockLlmService : ILlmService
    {
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmHealthResult
            {
                IsHealthy = true,
                Message = "Mock LLM service is healthy",
                ModelName = "test-model"
            });

        public Task<string> GenerateTitle(string content, string contentType, string[]? existingTags = null, int maxTitleLength = 100, CancellationToken cancellationToken = default)
            => Task.FromResult("Mock Generated Title");

        public void Dispose() { }
    }

} 