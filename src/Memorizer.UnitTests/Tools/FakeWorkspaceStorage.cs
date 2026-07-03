using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Pgvector;

namespace Memorizer.UnitTests.Tools;

/// <summary>
/// Fake storage that implements just enough of IStorage for workspace/project tool tests.
/// Uses manual fakes instead of a mocking framework.
/// </summary>
internal class FakeWorkspaceStorage : IStorage
{
    public Workspace? Workspace { get; set; }
    public Workspace? CreatedWorkspace { get; set; }
    public Workspace? WorkspaceBySlug { get; set; }
    public Project? Project { get; set; }
    public Project? CreatedProject { get; set; }

    // Captured arguments for slug lookups
    public string? LastSlugQueried { get; private set; }
    public WorkspaceId? LastParentIdQueried { get; private set; }

    // Workspace operations used by tests
    public Task<Workspace?> GetWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default)
        => Task.FromResult(Workspace);

    public Task<Workspace> CreateWorkspaceAsync(string name, string? description = null, WorkspaceId? parentId = null, CancellationToken cancellationToken = default)
        => Task.FromResult(CreatedWorkspace ?? throw new InvalidOperationException("CreatedWorkspace not set"));

    public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(WorkspaceId? parentId = null, bool includeSystem = false, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Workspace>>(new List<Workspace>());

    public Task<IReadOnlyList<WorkspacePathSegment>> GetWorkspacePathAsync(WorkspaceId id, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkspacePathSegment>>(new List<WorkspacePathSegment>());

    public Task<Workspace> UpdateWorkspaceAsync(WorkspaceId id, string? name = null, string? description = null, WorkspaceId? newParentId = null, bool makeTopLevel = false, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<Project> MoveProjectToWorkspaceAsync(ProjectId id, WorkspaceId newWorkspaceId, ProjectId? newParentId = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task DeleteWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<WorkspaceSearchResult>> SearchWorkspacesAsync(string query, bool includeSystem = false, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkspaceSearchResult>>(new List<WorkspaceSearchResult>());

    public Task<Workspace?> GetWorkspaceBySlugAsync(string slug, WorkspaceId? parentId = null, CancellationToken cancellationToken = default)
    {
        LastSlugQueried = slug;
        LastParentIdQueried = parentId;
        return Task.FromResult(WorkspaceBySlug);
    }

    // Project operations used by tests
    public Task<Project?> GetProjectAsync(ProjectId id, CancellationToken cancellationToken = default)
        => Task.FromResult(Project);

    public Task<Project> CreateProjectAsync(WorkspaceId workspaceId, string name, string? description = null, ProjectId? parentId = null, CancellationToken cancellationToken = default)
        => Task.FromResult(CreatedProject ?? throw new InvalidOperationException("CreatedProject not set"));

    public Task<IReadOnlyList<Project>> GetProjectsAsync(WorkspaceId workspaceId, ProjectId? parentId = null, ProjectStatusEnum? statusFilter = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Project>>(new List<Project>());

    public Task<ProjectPath> GetProjectPathAsync(ProjectId id, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProjectPath { WorkspacePath = Array.Empty<WorkspacePathSegment>(), ProjectAncestors = Array.Empty<ProjectPathSegment>() });

    public Task<Project> UpdateProjectAsync(ProjectId id, string? name = null, string? description = null, ProjectStatusEnum? status = null, string? victoryConditions = null, ProjectId? newParentId = null, bool makeTopLevel = false, CancellationToken cancellationToken = default)
        => Task.FromResult(CreatedProject ?? throw new InvalidOperationException("CreatedProject not set"));

    public Task DeleteProjectAsync(ProjectId id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<ProjectSearchResult>> SearchProjectsAsync(string query, ProjectStatusEnum? statusFilter = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ProjectSearchResult>>(new List<ProjectSearchResult>());

    // Memory operations (stubs)
    public Task<Memory> StoreMemory(string type, string content, string source, string[]? tags, Confidence confidence, string title, MemoryId? relatedTo = null, string? relationshipType = null, MemoryOwner? owner = null, ArchetypeEnum archetype = ArchetypeEnum.Document, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<Memory?> Get(MemoryId id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<List<Memory>> GetMany(IEnumerable<MemoryId> ids, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<bool> Delete(MemoryId id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<Memory?> UpdateMemory(MemoryId id, string type, string content, string source, string[]? tags, Confidence confidence, string? title, CancellationToken cancellationToken)
        => throw new NotImplementedException();
    public Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, SimilarityScore? score, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<List<MemoryRelationship>> GetRelationships(MemoryId memoryId, string? type = null, bool includeArchivedTargets = false, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<MemoryRelationship>());
    public Task<List<SimilarMemory>> GetSimilarMemories(MemoryId memoryId, SimilarityScore? minSimilarity = null, int limit = 10, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<SimilarMemory>());
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
    public Task<List<Memory>> Search(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, bool includeArchived = false, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<(List<Memory> Memories, int TotalCount)> GetMemoriesPaginated(int page = 1, int pageSize = 20, string? memoryType = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<List<string>> GetDistinctMemoryTypes(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<List<string>> GetDistinctTagsAsync(MemoryOwner? owner = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<List<MemoryOwner>> GetDistinctOwnersAsync(string[]? tags = null, string? memoryType = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<MemoryOwner>());
    public Task<List<Memory>> GetMemoriesWithoutTitles(int limit = 50, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task UpdateMemoryTitle(MemoryId id, string title, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task UpdateMemoryOwner(MemoryId id, MemoryOwner owner, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task SetMemoryOwnerAsync(MemoryId memoryId, MemoryOwner owner, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task MoveMemoryToUnfiledAsync(MemoryId memoryId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task<int> CountMemoriesWithoutMetadataEmbeddings(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<List<Memory>> GetMemoriesWithoutMetadataEmbeddings(int limit, bool includeExisting = false, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task UpdateMemoryMetadataEmbedding(MemoryId memoryId, Vector embedding, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task UpdateMemoryEmbeddings(MemoryId memoryId, Vector contentEmbedding, Vector metadataEmbedding, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<List<Memory>> SearchWithFullEmbedding(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, bool includeArchived = false, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<List<Memory>> SearchWithMetadataEmbedding(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, ProjectId? projectId = null, bool includeUnassigned = false, bool includeArchived = false, bool includeSystem = false, WorkspaceId? workspaceId = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<(List<Memory> FullResults, List<Memory> MetadataResults)> CompareSearchMethods(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<List<Memory>> HybridSearch(string query, int limit = 10, SimilarityScore? minSimilarity = null, string[]? filterTags = null, ProjectId? projectId = null, bool includeUnassigned = false, bool includeArchived = false, bool includeSystem = false, WorkspaceId? workspaceId = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<Memory?> UpdateMemoryArchetypeAsync(MemoryId memoryId, ArchetypeEnum newArchetype, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<(IReadOnlyList<Memory> Memories, int TotalCount)> GetArchivedMemoriesAsync(int page = 1, int pageSize = 50, ProjectId? projectId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<(IReadOnlyList<Memory> Memories, int TotalCount)>((new List<Memory>(), 0));
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
