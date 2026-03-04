using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using PostgMem.Tools;
using Pgvector;
using System.Text.Json;

namespace Memorizer.UnitTests.Tools;

/// <summary>
/// Tests that verify MemoryTools MCP responses include canonical URLs when configured.
/// Uses manual fakes instead of mocking framework.
/// </summary>
public class MemoryToolsCanonicalUrlTests
{
    private const string TestCanonicalUrl = "https://memory.testlab.petabridge.net";

    [Fact]
    public async Task Store_ShouldIncludeCanonicalUrl_WhenConfigured()
    {
        // Arrange
        var memoryId = new MemoryId(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));
        var fakeStorage = new FakeStorage
        {
            StoredMemory = CreateTestMemory(memoryId, "Test Memory")
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = true,
            BaseUrl = TestCanonicalUrl
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.Store(
            type: "reference",
            text: "Test content",
            source: "LLM",
            title: "Test Memory");

        // Assert
        Assert.Contains(TestCanonicalUrl, result);
        Assert.Contains("/view/a1b2c3d4-e5f6-7890-abcd-ef1234567890", result);
        Assert.True(fakeUrlService.GetMemoryUrlCalled);
    }

    [Fact]
    public async Task Store_ShouldNotIncludeCanonicalUrl_WhenNotConfigured()
    {
        // Arrange
        var memoryId = new MemoryId(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));
        var fakeStorage = new FakeStorage
        {
            StoredMemory = CreateTestMemory(memoryId, "Test Memory")
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = false
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.Store(
            type: "reference",
            text: "Test content",
            source: "LLM",
            title: "Test Memory");

        // Assert
        Assert.DoesNotContain(TestCanonicalUrl, result);
        Assert.DoesNotContain("View in web UI", result);
    }

    [Fact]
    public async Task SearchMemories_ShouldIncludeCanonicalUrl_ForEachResult_WhenConfigured()
    {
        // Arrange
        var fakeStorage = new FakeStorage
        {
            HybridSearchResults = new List<Memory>
            {
                CreateTestMemory(new MemoryId(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890")), "Memory 1"),
                CreateTestMemory(new MemoryId(Guid.Parse("b2c3d4e5-f6a7-8901-bcde-f23456789012")), "Memory 2")
            }
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = true,
            BaseUrl = TestCanonicalUrl
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.SearchMemories("test query");

        // Assert
        Assert.Contains(TestCanonicalUrl, result);
        Assert.Contains("/view/a1b2c3d4-e5f6-7890-abcd-ef1234567890", result);
        Assert.Contains("/view/b2c3d4e5-f6a7-8901-bcde-f23456789012", result);
        Assert.Equal(2, fakeUrlService.GetMemoryUrlCallCount);
    }

    [Fact]
    public async Task Get_ShouldIncludeCanonicalUrl_WhenConfigured()
    {
        // Arrange
        var memoryId = new MemoryId(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));
        var fakeStorage = new FakeStorage
        {
            RetrievedMemory = CreateTestMemory(memoryId, "Test Memory")
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = true,
            BaseUrl = TestCanonicalUrl
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.Get(memoryId.Value, includeSimilar: false);

        // Assert
        Assert.Contains(TestCanonicalUrl, result);
        Assert.Contains("/view/a1b2c3d4-e5f6-7890-abcd-ef1234567890", result);
        Assert.True(fakeUrlService.GetMemoryUrlCalled);
    }

    [Fact]
    public async Task GetMany_ShouldIncludeCanonicalUrl_ForEachMemory_WhenConfigured()
    {
        // Arrange
        var memoryIds = new[]
        {
            Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"),
            Guid.Parse("b2c3d4e5-f6a7-8901-bcde-f23456789012")
        };
        var fakeStorage = new FakeStorage
        {
            ManyResults = memoryIds.Select(id => CreateTestMemory(new MemoryId(id), $"Memory {id}")).ToList()
        };
        var fakeUrlService = new FakeCanonicalUrlService
        {
            IsConfigured = true,
            BaseUrl = TestCanonicalUrl
        };
        var tools = CreateTools(fakeStorage, fakeUrlService);

        // Act
        var result = await tools.GetMany(memoryIds);

        // Assert
        Assert.Contains(TestCanonicalUrl, result);
        Assert.Contains("/view/a1b2c3d4-e5f6-7890-abcd-ef1234567890", result);
        Assert.Contains("/view/b2c3d4e5-f6a7-8901-bcde-f23456789012", result);
        Assert.Equal(2, fakeUrlService.GetMemoryUrlCallCount);
    }

    private static MemoryTools CreateTools(FakeStorage storage, FakeCanonicalUrlService urlService)
    {
        var searchSettings = new SearchSettings { ReturnFullContent = false };
        var logger = new NullLogger<MemoryTools>();
        return new MemoryTools(storage, logger, searchSettings, urlService);
    }

    private static Memory CreateTestMemory(MemoryId id, string title)
    {
        return new Memory
        {
            Id = id,
            Type = "reference",
            Text = "Test content",
            Source = "test",
            Tags = [],
            Confidence = new Confidence(1.0),
            Embedding = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Title = title,
            Owner = MemoryOwner.Unfiled,
            Archetype = ArchetypeEnum.Document,
            CurrentVersion = new VersionNumber(1)
        };
    }

    /// <summary>
    /// Fake storage implementing IStorage with just enough for memory tool tests
    /// </summary>
    private class FakeStorage : IStorage
    {
        public Memory? StoredMemory { get; set; }
        public Memory? RetrievedMemory { get; set; }
        public List<Memory>? HybridSearchResults { get; set; }
        public List<Memory>? ManyResults { get; set; }

        // Core memory operations used by tests
        public Task<Memory> StoreMemory(string type, string content, string source, string[]? tags, Confidence confidence, string title, MemoryId? relatedTo = null, string? relationshipType = null, MemoryOwner? owner = null, ArchetypeEnum archetype = ArchetypeEnum.Document, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredMemory ?? throw new InvalidOperationException("StoredMemory not set"));

        public Task<Memory?> Get(MemoryId id, CancellationToken cancellationToken = default)
            => Task.FromResult(RetrievedMemory);

        public Task<List<Memory>> GetMany(IEnumerable<MemoryId> ids, CancellationToken cancellationToken = default)
            => Task.FromResult(ManyResults ?? new List<Memory>());

        public Task<List<Memory>> HybridSearch(string query, int limit, SimilarityScore? minSimilarity, string[]? filterTags, ProjectId? projectId, bool includeUnassigned, bool includeArchived, bool includeSystem, CancellationToken cancellationToken)
            => Task.FromResult(HybridSearchResults ?? new List<Memory>());

        // Stub implementations for remaining interface members - grouped by category
        
        // Basic CRUD
        public Task<bool> Delete(MemoryId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        
        public Task<Memory?> UpdateMemory(MemoryId id, string type, string content, string source, string[]? tags, Confidence confidence, string? title, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        // Search methods
        public Task<List<Memory>> Search(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, bool includeArchived = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<Memory>> SearchWithFullEmbedding(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, bool includeArchived = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<Memory>> SearchWithMetadataEmbedding(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, ProjectId? projectId = null, bool includeUnassigned = false, bool includeArchived = false, bool includeSystem = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<(List<Memory> FullResults, List<Memory> MetadataResults)> CompareSearchMethods(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        // Relationships
        public Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, SimilarityScore? score, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<MemoryRelationship>> GetRelationships(MemoryId memoryId, string? type = null, bool includeArchivedTargets = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<MemoryRelationship>());
        public Task<List<SimilarMemory>> GetSimilarMemories(MemoryId memoryId, SimilarityScore? minSimilarity = null, int limit = 10, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<SimilarMemory>());

        // Versioning
        public Task<List<MemoryEvent>> GetEvents(MemoryId memoryId, int? limit = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<MemoryVersion>> GetVersionHistory(MemoryId memoryId, int? limit = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<MemoryVersion?> GetVersion(MemoryId memoryId, VersionNumber versionNumber, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Memory?> RevertToVersion(MemoryId memoryId, VersionNumber versionNumber, string? changedBy = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<int> PurgeVersionsKeepingLatest(MemoryId memoryId, int versionsToKeep, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<int> PurgeVersionsOlderThan(DateTime cutoffDate, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<VersionStats> GetVersionStats(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        // Pagination and listing
        public Task<(List<Memory> Memories, int TotalCount)> GetMemoriesPaginated(int page = 1, int pageSize = 20, string? memoryType = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<string>> GetDistinctMemoryTypes(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<string>> GetDistinctTagsAsync(MemoryOwner? owner = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<MemoryOwner>> GetDistinctOwnersAsync(string[]? tags = null, string? memoryType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<MemoryOwner>());

        // Title generation
        public Task<List<Memory>> GetMemoriesWithoutTitles(int limit = 50, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task UpdateMemoryTitle(MemoryId id, string title, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        // Owner operations
        public Task UpdateMemoryOwner(MemoryId id, MemoryOwner owner, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task SetMemoryOwnerAsync(MemoryId memoryId, MemoryOwner owner, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task MoveMemoryToUnfiledAsync(MemoryId memoryId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // Metadata embeddings
        public Task<int> CountMemoriesWithoutMetadataEmbeddings(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<Memory>> GetMemoriesWithoutMetadataEmbeddings(int limit, bool includeExisting = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task UpdateMemoryMetadataEmbedding(MemoryId memoryId, Vector embedding, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task UpdateMemoryEmbeddings(MemoryId memoryId, Vector contentEmbedding, Vector metadataEmbedding, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        // Archetype/Archival
        public Task<Memory?> UpdateMemoryArchetypeAsync(MemoryId memoryId, ArchetypeEnum newArchetype, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<Memory> Memories, int TotalCount)> GetArchivedMemoriesAsync(int page = 1, int pageSize = 50, ProjectId? projectId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<(IReadOnlyList<Memory> Memories, int TotalCount)>((new List<Memory>(), 0));

        // Owner-based queries
        public Task<IReadOnlyList<Memory>> GetMemoriesByOwnerAsync(MemoryOwner owner, int page = 1, int pageSize = 50, string? memoryType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Memory>>(new List<Memory>());
        public Task<int> GetMemoryCountByOwnerAsync(MemoryOwner owner, string? memoryType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
        public Task<IReadOnlyList<Memory>> GetUnfiledMemoriesAsync(int page = 1, int pageSize = 50, string? memoryType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Memory>>(new List<Memory>());
        public Task<int> GetUnfiledMemoryCountAsync(string? memoryType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
        public Task<(IReadOnlyList<Memory> Memories, int TotalCount)> GetMemoriesByTagAsync(string[] tags, int page = 1, int pageSize = 20, MemoryOwner? owner = null, string? memoryType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<(IReadOnlyList<Memory>, int)>((new List<Memory>(), 0));

        // Workspace operations
        public Task<Workspace> CreateWorkspaceAsync(string name, string? description = null, WorkspaceId? parentId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Workspace?> GetWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Workspace?> GetWorkspaceBySlugAsync(string slug, WorkspaceId? parentId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(WorkspaceId? parentId = null, bool includeSystem = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Workspace> UpdateWorkspaceAsync(WorkspaceId id, string? name = null, string? description = null, WorkspaceId? newParentId = null, bool makeTopLevel = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Project> MoveProjectToWorkspaceAsync(ProjectId id, WorkspaceId newWorkspaceId, ProjectId? newParentId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task DeleteWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkspaceSearchResult>> SearchWorkspacesAsync(string query, bool includeSystem = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkspacePathSegment>> GetWorkspacePathAsync(WorkspaceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspacePathSegment>>(new List<WorkspacePathSegment>());

        // Project operations
        public Task<Project> CreateProjectAsync(WorkspaceId workspaceId, string name, string? description = null, ProjectId? parentId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Project?> GetProjectAsync(ProjectId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Project>> GetProjectsAsync(WorkspaceId workspaceId, ProjectId? parentId = null, ProjectStatusEnum? statusFilter = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Project> UpdateProjectAsync(ProjectId id, string? name = null, string? description = null, ProjectStatusEnum? status = null, string? victoryConditions = null, ProjectId? newParentId = null, bool makeTopLevel = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ProjectSearchResult>> SearchProjectsAsync(string query, ProjectStatusEnum? statusFilter = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ProjectPath> GetProjectPathAsync(ProjectId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        // Provider settings
        public Task<ProviderSettings?> GetActiveProviderAsync(string providerType, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderSettings>> GetAllProvidersAsync(string providerType, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ProviderSettings> SaveProviderSettingsAsync(ProviderSettings settings, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task SetActiveProviderAsync(string providerType, string providerName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // System memory seeding
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

    private class FakeCanonicalUrlService : ICanonicalUrlService
    {
        public bool IsConfigured { get; set; }
        public string BaseUrl { get; set; } = "";
        public bool GetMemoryUrlCalled { get; private set; }
        public int GetMemoryUrlCallCount { get; private set; }
        public bool GetWorkspaceUrlCalled { get; private set; }
        public bool GetProjectUrlCalled { get; private set; }

        public string? GetMemoryUrl(MemoryId memoryId)
        {
            GetMemoryUrlCalled = true;
            GetMemoryUrlCallCount++;
            return IsConfigured ? $"{BaseUrl.TrimEnd('/')}/view/{memoryId.Value}" : null;
        }

        public string? GetWorkspaceUrl(WorkspaceId workspaceId)
        {
            GetWorkspaceUrlCalled = true;
            return IsConfigured ? $"{BaseUrl.TrimEnd('/')}/workspace/{workspaceId.Value}" : null;
        }

        public string? GetProjectUrl(ProjectId projectId)
        {
            GetProjectUrlCalled = true;
            return IsConfigured ? $"{BaseUrl.TrimEnd('/')}/project/{projectId.Value}" : null;
        }
    }
}
