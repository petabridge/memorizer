using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Memorizer.Actors;
using Memorizer.Models;
using Memorizer.Models.ValueTypes;
using Memorizer.Models.Enums;
using Memorizer.Services;
using Memorizer.Services.Providers;
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

        // Add mock services - use MockStorageWithMemories to test actual batch processing
        services.AddSingleton<IStorage, MockStorageWithMemories>();
        services.AddSingleton<IMemorizerAgentProvider, MockMemorizerAgentProvider>();
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
        // Create a new actor for this test using the resolver from Akka.DependencyInjection
        var resolver = Akka.DependencyInjection.DependencyResolver.For(Sys);
        var testActor = Sys.ActorOf(resolver.Props<TitleGenerationActor>(), "title-generation-test");

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
                new() { Id = MemoryId.New(), Text = "Test memory 1", Type = "test", Tags = [] },
                new() { Id = MemoryId.New(), Text = "Test memory 2", Type = "test", Tags = [] },
                new() { Id = MemoryId.New(), Text = "Test memory 3", Type = "test", Tags = [] }
            };
            return Task.FromResult(memories);
        }

        public Task UpdateMemoryTitle(MemoryId id, string title, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<Memory> StoreMemory(string type, string content, string source, string[]? tags, Confidence confidence, string title, MemoryId? relatedTo = null, string? relationshipType = null, MemoryOwner? owner = null, ArchetypeEnum archetype = ArchetypeEnum.Document, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<Memory?> Get(MemoryId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> GetMany(IEnumerable<MemoryId> ids, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<bool> Delete(MemoryId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> Search(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, bool includeArchived = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<(List<Memory> Memories, int TotalCount)> GetMemoriesPaginated(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<Memory?> UpdateMemory(MemoryId id, string type, string content, string source, string[]? tags, Confidence confidence, string? title = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task UpdateMemoryOwner(MemoryId id, MemoryOwner owner, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, SimilarityScore? score, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<MemoryRelationship>> GetRelationships(MemoryId memoryId, string? type = null, bool includeArchivedTargets = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<SimilarMemory>> GetSimilarMemories(MemoryId memoryId, SimilarityScore? minSimilarity = null, int limit = 10, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<SimilarMemory>());

        public Task<List<string>> GetDistinctMemoryTypes(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string> { "test", "mock" });

        public Task<int> CountMemoriesWithoutMetadataEmbeddings(CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<List<Memory>> GetMemoriesWithoutMetadataEmbeddings(int limit, bool includeExisting = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<Memory>());

        public Task UpdateMemoryMetadataEmbedding(MemoryId memoryId, Vector embedding, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateMemoryEmbeddings(MemoryId memoryId, Vector contentEmbedding, Vector metadataEmbedding, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<List<Memory>> SearchWithFullEmbedding(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, bool includeArchived = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> SearchWithMetadataEmbedding(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, ProjectId? projectId = null, bool includeUnassigned = false, bool includeArchived = false, bool includeSystem = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<(List<Memory> FullResults, List<Memory> MetadataResults)> CompareSearchMethods(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<List<Memory>> HybridSearch(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, ProjectId? projectId = null, bool includeUnassigned = false, bool includeArchived = false, bool includeSystem = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        // Versioning support
        public Task<List<MemoryEvent>> GetEvents(MemoryId memoryId, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<MemoryEvent>());

        public Task<List<MemoryVersion>> GetVersionHistory(MemoryId memoryId, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<MemoryVersion>());

        public Task<MemoryVersion?> GetVersion(MemoryId memoryId, VersionNumber versionNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<MemoryVersion?>(null);

        public Task<Memory?> RevertToVersion(MemoryId memoryId, VersionNumber versionNumber, string? changedBy = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<int> PurgeVersionsKeepingLatest(MemoryId memoryId, int versionsToKeep, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<int> PurgeVersionsOlderThan(DateTime cutoffDate, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<VersionStats> GetVersionStats(CancellationToken cancellationToken = default)
            => Task.FromResult(new VersionStats());

        // Provider settings
        public Task<ProviderSettings?> GetActiveProviderAsync(string providerType, CancellationToken cancellationToken = default)
            => Task.FromResult<ProviderSettings?>(null);

        public Task<IReadOnlyList<ProviderSettings>> GetAllProvidersAsync(string providerType, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProviderSettings>>(new List<ProviderSettings>());

        public Task<ProviderSettings> SaveProviderSettingsAsync(ProviderSettings settings, CancellationToken cancellationToken = default)
            => Task.FromResult(settings);

        public Task SetActiveProviderAsync(string providerType, string providerName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // Workspace operations
        public Task<Workspace> CreateWorkspaceAsync(string name, string? description = null, WorkspaceId? parentId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<Workspace?> GetWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<Workspace?>(null);

        public Task<Workspace?> GetWorkspaceBySlugAsync(string slug, WorkspaceId? parentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<Workspace?>(null);

        public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(WorkspaceId? parentId = null, bool includeSystem = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Workspace>>(new List<Workspace>());

        public Task<Workspace> UpdateWorkspaceAsync(WorkspaceId id, string? name = null, string? description = null, WorkspaceId? newParentId = null, bool makeTopLevel = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<Project> MoveProjectToWorkspaceAsync(ProjectId id, WorkspaceId newWorkspaceId, ProjectId? newParentId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task DeleteWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        // Project operations
        public Task<Project> CreateProjectAsync(WorkspaceId workspaceId, string name, string? description = null, ProjectId? parentId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<Project?> GetProjectAsync(ProjectId id, CancellationToken cancellationToken = default)
            => Task.FromResult<Project?>(null);

        public Task<IReadOnlyList<Project>> GetProjectsAsync(WorkspaceId workspaceId, ProjectId? parentId = null, ProjectStatusEnum? statusFilter = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Project>>(new List<Project>());

        public Task<Project> UpdateProjectAsync(ProjectId id, string? name = null, string? description = null, ProjectStatusEnum? status = null, string? victoryConditions = null, ProjectId? newParentId = null, bool makeTopLevel = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task DeleteProjectAsync(ProjectId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        // Memory owner operations
        public Task SetMemoryOwnerAsync(MemoryId memoryId, MemoryOwner owner, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MoveMemoryToUnfiledAsync(MemoryId memoryId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Memory>> GetMemoriesByOwnerAsync(MemoryOwner owner, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Memory>>(new List<Memory>());

        public Task<int> GetMemoryCountByOwnerAsync(MemoryOwner owner, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyList<Memory>> GetUnfiledMemoriesAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Memory>>(new List<Memory>());

        public Task<int> GetUnfiledMemoryCountAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        // Archival operations
        public Task<Memory?> UpdateMemoryArchetypeAsync(MemoryId memoryId, ArchetypeEnum newArchetype, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Test mock");

        public Task<(IReadOnlyList<Memory> Memories, int TotalCount)> GetArchivedMemoriesAsync(int page = 1, int pageSize = 50, ProjectId? projectId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<(IReadOnlyList<Memory>, int)>((new List<Memory>(), 0));

        // Search operations
        public Task<IReadOnlyList<WorkspaceSearchResult>> SearchWorkspacesAsync(string query, bool includeSystem = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceSearchResult>>(new List<WorkspaceSearchResult>());

        public Task<IReadOnlyList<WorkspacePathSegment>> GetWorkspacePathAsync(WorkspaceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspacePathSegment>>(new List<WorkspacePathSegment>());

        public Task<IReadOnlyList<ProjectSearchResult>> SearchProjectsAsync(string query, ProjectStatusEnum? statusFilter = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProjectSearchResult>>(new List<ProjectSearchResult>());

        public Task<ProjectPath> GetProjectPathAsync(ProjectId id, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProjectPath { WorkspacePath = [], ProjectAncestors = [] });

        public Task<(int ProjectsSeeded, int WorkspacesSeeded)> SeedProjectAndWorkspaceSystemMemoriesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((0, 0));

        // Data migration tracking
        public Task<bool> HasDataMigrationRunAsync(string migrationName, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task RecordDataMigrationAsync(string migrationName, string? description = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> ExecuteDataMigrationIfNeededAsync(string migrationName, string description, Func<CancellationToken, Task> migrationAction, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    /// <summary>
    /// Minimal mock Memorizer Agent provider for testing
    /// </summary>
    private class MockMemorizerAgentProvider : IMemorizerAgentProvider
    {
        public string ProviderName => "mock";
        public string DisplayName => "Mock Provider";

        public Task<MemorizerAgentHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemorizerAgentHealthResult
            {
                IsHealthy = true,
                Message = "Mock Memorizer Agent is healthy",
                ModelName = "test-model"
            });

        public Task<string> GenerateTitleAsync(string content, string contentType, string[]? existingTags = null, int maxTitleLength = 100, CancellationToken cancellationToken = default)
            => Task.FromResult("Mock Generated Title");

        public void Dispose() { }
    }

} 