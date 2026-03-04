using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Settings;
using Npgsql;
using Pgvector;
using Registrator.Net;
using MemoryRelationship = Memorizer.Models.MemoryRelationship;
using MemoryEvent = Memorizer.Models.MemoryEvent;
using MemoryVersion = Memorizer.Models.MemoryVersion;
using SimilarMemory = Memorizer.Models.SimilarMemory;

namespace Memorizer.Services;

public interface IStorage
{
    Task<Memorizer.Models.Memory> StoreMemory(
        string type,
        string content,
        string source,
        string[]? tags,
        Confidence confidence,
        string title,
        MemoryId? relatedTo = null,
        string? relationshipType = null,
        MemoryOwner? owner = null,
        ArchetypeEnum archetype = ArchetypeEnum.Document,
        CancellationToken cancellationToken = default
    );

    Task<List<Memorizer.Models.Memory>> Search(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default
    );

    Task<Memorizer.Models.Memory?> Get(
        MemoryId id,
        CancellationToken cancellationToken = default
    );

    Task<bool> Delete(
        MemoryId id,
        CancellationToken cancellationToken = default
    );

    Task<List<Memorizer.Models.Memory>> GetMany(IEnumerable<MemoryId> ids, CancellationToken cancellationToken = default);
    Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, CancellationToken cancellationToken = default);
    Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, SimilarityScore? score, CancellationToken cancellationToken = default);
    Task<List<MemoryRelationship>> GetRelationships(MemoryId memoryId, string? type = null, bool includeArchivedTargets = false, CancellationToken cancellationToken = default);

    // Similarity discovery
    Task<List<SimilarMemory>> GetSimilarMemories(MemoryId memoryId, SimilarityScore? minSimilarity = null, int limit = 10, CancellationToken cancellationToken = default);

    // Pagination support
    Task<(List<Memorizer.Models.Memory> Memories, int TotalCount)> GetMemoriesPaginated(
        int page = 1,
        int pageSize = 20,
        string? memoryType = null,
        CancellationToken cancellationToken = default
    );

    // Update existing memory
    Task<Memorizer.Models.Memory?> UpdateMemory(
        MemoryId id,
        string type,
        string content,
        string source,
        string[]? tags,
        Confidence confidence,
        string? title = null,
        CancellationToken cancellationToken = default
    );

    // Get distinct memory types
    Task<List<string>> GetDistinctMemoryTypes(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all distinct tags across all non-archived memories, sorted alphabetically.
    /// </summary>
    Task<List<string>> GetDistinctTagsAsync(MemoryOwner? owner = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets distinct owner (type, id) pairs for memories matching the given filters.
    /// </summary>
    Task<List<MemoryOwner>> GetDistinctOwnersAsync(string[]? tags = null, string? memoryType = null, CancellationToken cancellationToken = default);

    // Title generation support
    Task<List<Memorizer.Models.Memory>> GetMemoriesWithoutTitles(
        int limit = 50,
        CancellationToken cancellationToken = default
    );

    Task UpdateMemoryTitle(
        MemoryId id,
        string title,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update the owner (workspace or project) of a memory
    /// </summary>
    Task UpdateMemoryOwner(
        MemoryId id,
        MemoryOwner owner,
        CancellationToken cancellationToken = default
    );

    // Metadata embedding support
    Task<int> CountMemoriesWithoutMetadataEmbeddings(CancellationToken cancellationToken = default);
    Task<List<Memorizer.Models.Memory>> GetMemoriesWithoutMetadataEmbeddings(int limit, bool includeExisting = false, CancellationToken cancellationToken = default);
    Task UpdateMemoryMetadataEmbedding(MemoryId memoryId, Vector embedding, CancellationToken cancellationToken = default);

    // Combined embedding update (for re-embedding when dimensions change)
    Task UpdateMemoryEmbeddings(MemoryId memoryId, Vector contentEmbedding, Vector metadataEmbedding, CancellationToken cancellationToken = default);

    // Dual embedding comparison methods for PoC
    Task<List<Memorizer.Models.Memory>> SearchWithFullEmbedding(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default
    );

    Task<List<Memorizer.Models.Memory>> SearchWithMetadataEmbedding(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        ProjectId? projectId = null,
        bool includeUnassigned = false,
        bool includeArchived = false,
        bool includeSystem = false,
        CancellationToken cancellationToken = default
    );

    Task<(List<Memorizer.Models.Memory> FullResults, List<Memorizer.Models.Memory> MetadataResults)> CompareSearchMethods(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    );

    Task<List<Memorizer.Models.Memory>> HybridSearch(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        ProjectId? projectId = null,
        bool includeUnassigned = false,
        bool includeArchived = false,
        bool includeSystem = false,
        CancellationToken cancellationToken = default
    );

    // Versioning support
    Task<List<MemoryEvent>> GetEvents(MemoryId memoryId, int? limit = null, CancellationToken cancellationToken = default);
    Task<List<MemoryVersion>> GetVersionHistory(MemoryId memoryId, int? limit = null, CancellationToken cancellationToken = default);
    Task<MemoryVersion?> GetVersion(MemoryId memoryId, VersionNumber versionNumber, CancellationToken cancellationToken = default);
    Task<Memorizer.Models.Memory?> RevertToVersion(MemoryId memoryId, VersionNumber versionNumber, string? changedBy = null, CancellationToken cancellationToken = default);

    // Admin version operations
    Task<int> PurgeVersionsKeepingLatest(MemoryId memoryId, int versionsToKeep, CancellationToken cancellationToken = default);
    Task<int> PurgeVersionsOlderThan(DateTime cutoffDate, CancellationToken cancellationToken = default);
    Task<VersionStats> GetVersionStats(CancellationToken cancellationToken = default);

    // Provider settings
    Task<ProviderSettings?> GetActiveProviderAsync(string providerType, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderSettings>> GetAllProvidersAsync(string providerType, CancellationToken cancellationToken = default);
    Task<ProviderSettings> SaveProviderSettingsAsync(ProviderSettings settings, CancellationToken cancellationToken = default);
    Task SetActiveProviderAsync(string providerType, string providerName, CancellationToken cancellationToken = default);

    // ===== Workspace Operations =====

    /// <summary>
    /// Creates a new workspace.
    /// </summary>
    Task<Workspace> CreateWorkspaceAsync(
        string name,
        string? description = null,
        WorkspaceId? parentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a workspace by ID.
    /// </summary>
    Task<Workspace?> GetWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a workspace by slug within a parent scope.
    /// </summary>
    Task<Workspace?> GetWorkspaceBySlugAsync(string slug, WorkspaceId? parentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets workspaces, optionally filtered by parent and system status.
    /// </summary>
    Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(
        WorkspaceId? parentId = null,
        bool includeSystem = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates workspace properties. Can also reparent the workspace or promote it to top-level.
    /// </summary>
    Task<Workspace> UpdateWorkspaceAsync(
        WorkspaceId id,
        string? name = null,
        string? description = null,
        WorkspaceId? newParentId = null,
        bool makeTopLevel = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a project (and all its descendants) to a different workspace.
    /// </summary>
    /// <param name="id">The root project to move.</param>
    /// <param name="newWorkspaceId">The target workspace.</param>
    /// <param name="newParentId">Optional new parent project in the target workspace. Must belong to the target workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Project> MoveProjectToWorkspaceAsync(
        ProjectId id,
        WorkspaceId newWorkspaceId,
        ProjectId? newParentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a workspace. System workspaces cannot be deleted.
    /// </summary>
    Task DeleteWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches workspaces by name (case-insensitive substring match) across all levels.
    /// Returns matching workspaces with their ancestor path.
    /// </summary>
    Task<IReadOnlyList<WorkspaceSearchResult>> SearchWorkspacesAsync(
        string query,
        bool includeSystem = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the ancestor path for a workspace (from root to parent).
    /// Returns empty list for root workspaces.
    /// </summary>
    Task<IReadOnlyList<WorkspacePathSegment>> GetWorkspacePathAsync(
        WorkspaceId id,
        CancellationToken cancellationToken = default);

    // ===== Project Operations =====

    /// <summary>
    /// Creates a new project within a workspace.
    /// </summary>
    Task<Project> CreateProjectAsync(
        WorkspaceId workspaceId,
        string name,
        string? description = null,
        ProjectId? parentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a project by ID.
    /// </summary>
    Task<Project?> GetProjectAsync(ProjectId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets projects in a workspace, optionally filtered by parent and status.
    /// </summary>
    Task<IReadOnlyList<Project>> GetProjectsAsync(
        WorkspaceId workspaceId,
        ProjectId? parentId = null,
        ProjectStatusEnum? statusFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates project properties.
    /// </summary>
    /// <param name="id">The project ID to update.</param>
    /// <param name="name">New name (null = keep current).</param>
    /// <param name="description">New description (null = keep current).</param>
    /// <param name="status">New status (null = keep current).</param>
    /// <param name="victoryConditions">New victory conditions (null = keep current).</param>
    /// <param name="newParentId">New parent project ID to move under. If specified, moves the project under this parent.</param>
    /// <param name="makeTopLevel">If true, removes the project from its current parent (makes it top-level in its workspace).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Moving projects: Specify either newParentId or makeTopLevel, not both.
    /// Circular references are prevented - a project cannot be moved under its own descendant.
    /// </remarks>
    Task<Project> UpdateProjectAsync(
        ProjectId id,
        string? name = null,
        string? description = null,
        ProjectStatusEnum? status = null,
        string? victoryConditions = null,
        ProjectId? newParentId = null,
        bool makeTopLevel = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a project.
    /// </summary>
    Task DeleteProjectAsync(ProjectId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches projects by name (case-insensitive substring match) across all workspaces.
    /// Returns matching projects with their full path (workspace ancestry + project ancestry).
    /// </summary>
    Task<IReadOnlyList<ProjectSearchResult>> SearchProjectsAsync(
        string query,
        ProjectStatusEnum? statusFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the full path for a project (workspace ancestry + project ancestry).
    /// </summary>
    Task<ProjectPath> GetProjectPathAsync(
        ProjectId id,
        CancellationToken cancellationToken = default);

    // ===== Memory Owner Operations =====

    /// <summary>
    /// Sets the owner of a memory (workspace or project).
    /// </summary>
    Task SetMemoryOwnerAsync(MemoryId memoryId, MemoryOwner owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a memory to the Unfiled workspace.
    /// </summary>
    Task MoveMemoryToUnfiledAsync(MemoryId memoryId, CancellationToken cancellationToken = default);

    // ===== Archival Operations =====

    /// <summary>
    /// Updates a memory's archetype (document, record, or archived).
    /// Use this to archive memories or restore them from archived status.
    /// </summary>
    Task<Memorizer.Models.Memory?> UpdateMemoryArchetypeAsync(
        MemoryId memoryId,
        ArchetypeEnum newArchetype,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets archived memories with pagination.
    /// </summary>
    Task<(IReadOnlyList<Memorizer.Models.Memory> Memories, int TotalCount)> GetArchivedMemoriesAsync(
        int page = 1,
        int pageSize = 50,
        ProjectId? projectId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets memories by owner (workspace or project) with pagination.
    /// </summary>
    Task<IReadOnlyList<Memorizer.Models.Memory>> GetMemoriesByOwnerAsync(
        MemoryOwner owner,
        int page = 1,
        int pageSize = 50,
        string? memoryType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of memories for an owner.
    /// </summary>
    Task<int> GetMemoryCountByOwnerAsync(MemoryOwner owner, string? memoryType = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets unfiled memories (convenience method).
    /// </summary>
    Task<IReadOnlyList<Memorizer.Models.Memory>> GetUnfiledMemoriesAsync(
        int page = 1,
        int pageSize = 50,
        string? memoryType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of unfiled memories.
    /// </summary>
    Task<int> GetUnfiledMemoryCountAsync(string? memoryType = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets memories matching one or more tags (case-insensitive, AND logic) with pagination.
    /// Optionally scoped to a specific owner (workspace/project).
    /// </summary>
    Task<(IReadOnlyList<Memorizer.Models.Memory> Memories, int TotalCount)> GetMemoriesByTagAsync(
        string[] tags,
        int page = 1,
        int pageSize = 20,
        MemoryOwner? owner = null,
        string? memoryType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds system memories for all projects and workspaces that don't have them.
    /// This enables semantic search on project/workspace metadata.
    /// Returns the count of system memories created.
    /// </summary>
    Task<(int ProjectsSeeded, int WorkspacesSeeded)> SeedProjectAndWorkspaceSystemMemoriesAsync(CancellationToken cancellationToken = default);

    // ===== Data Migration Tracking =====

    /// <summary>
    /// Checks if a data migration has already been executed.
    /// </summary>
    /// <param name="migrationName">Unique name for the migration (e.g., "seed_project_system_memories_v1").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the migration has already run, false otherwise.</returns>
    Task<bool> HasDataMigrationRunAsync(string migrationName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a data migration has been executed.
    /// </summary>
    /// <param name="migrationName">Unique name for the migration.</param>
    /// <param name="description">Human-readable description of what the migration does.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordDataMigrationAsync(string migrationName, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a data migration if it hasn't already been run.
    /// </summary>
    /// <param name="migrationName">Unique name for the migration.</param>
    /// <param name="description">Human-readable description.</param>
    /// <param name="migrationAction">The action to execute if the migration hasn't run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the migration was executed, false if it was skipped (already run).</returns>
    Task<bool> ExecuteDataMigrationIfNeededAsync(
        string migrationName,
        string description,
        Func<CancellationToken, Task> migrationAction,
        CancellationToken cancellationToken = default);
}

[AutoRegisterInterfaces(ServiceLifetime.Scoped)]
public class Storage : IStorage
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService _embeddingService;
    private readonly IDiffService _diffService;
    private readonly VersioningSettings _versioningSettings;
    private readonly IMarkdownExportService? _markdownExportService;

    public Storage(NpgsqlDataSource dataSource, IEmbeddingService embeddingService, IDiffService diffService, VersioningSettings versioningSettings, IMarkdownExportService? markdownExportService = null)
    {
        _dataSource = dataSource;
        _embeddingService = embeddingService;
        _diffService = diffService;
        _versioningSettings = versioningSettings;
        _markdownExportService = markdownExportService;
    }

    /// <summary>
    /// Prune old versions if we exceed MaxVersionsPerMemory setting.
    /// Deletes oldest versions first (FIFO). Must be called within a transaction.
    /// </summary>
    private async Task PruneOldVersionsIfNeeded(Guid memoryId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var maxVersions = _versioningSettings.MaxVersionsPerMemory;
        if (maxVersions <= 0)
            return; // Pruning disabled

        const string pruneSql = @"
            WITH versions_to_delete AS (
                SELECT version_id
                FROM memory_versions
                WHERE memory_id = @memoryId
                ORDER BY version_number DESC
                OFFSET @keepCount
            )
            DELETE FROM memory_versions
            WHERE version_id IN (SELECT version_id FROM versions_to_delete)";

        await using var cmd = new NpgsqlCommand(pruneSql, connection, transaction);
        cmd.Parameters.AddWithValue("memoryId", memoryId);
        cmd.Parameters.AddWithValue("keepCount", maxVersions);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Memorizer.Models.Memory> StoreMemory(
        string type,
        string content,
        string source,
        string[]? tags,
        Confidence confidence,
        string title,
        MemoryId? relatedTo = null,
        string? relationshipType = null,
        MemoryOwner? owner = null,
        ArchetypeEnum archetype = ArchetypeEnum.Document,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A title is required for all new memories.", nameof(title));

        // Determine whether the incoming string is JSON or plain text
        JsonDocument document;
        string bodyText;

        try
        {
            document = JsonDocument.Parse(content);

            // Attempt to extract a sensible text body from common keys; fallback to full JSON text
            bodyText =
                document.RootElement.TryGetProperty("text", out var tElem) && tElem.ValueKind == JsonValueKind.String
                    ? tElem.GetString() ?? content
                : document.RootElement.TryGetProperty("fact", out var fElem) && fElem.ValueKind == JsonValueKind.String
                    ? fElem.GetString() ?? content
                : document.RootElement.TryGetProperty("observation", out var oElem) && oElem.ValueKind == JsonValueKind.String
                    ? oElem.GetString() ?? content
                : document.RootElement.TryGetProperty("content", out var cElem) && cElem.ValueKind == JsonValueKind.String
                    ? cElem.GetString() ?? content
                : content;
        }
        catch (JsonException)
        {
            // Not valid JSON – treat entire string as text; store an empty JSON object for backwards compatibility
            document = JsonDocument.Parse("{}");
            bodyText = content;
        }

        string textToEmbed = bodyText;

        // Combine title and content for embedding if title is present
        if (!string.IsNullOrWhiteSpace(title))
        {
            textToEmbed = title + " " + textToEmbed;
        }

        // Generate full content embedding (current approach)
        float[] embedding = await _embeddingService.Generate(
            textToEmbed, // Use the combined title + content for embedding
            cancellationToken
        );
        
        // Generate metadata-only embedding (new approach for PoC)
        string metadataText = title;
        if (tags is { Length: > 0 })
        {
            metadataText += " " + string.Join(" ", tags);
        }
        
        float[] embeddingMetadata = await _embeddingService.Generate(
            metadataText, // Use only title + tags for embedding
            cancellationToken
        );
        
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Default to Unfiled workspace if no owner specified
        var memoryOwner = owner ?? MemoryOwner.Unfiled;

        Memorizer.Models.Memory memory = new()
        {
            Id = MemoryId.New(),
            Type = type,
            Content = document,
            Text = bodyText,
            Source = source,
            Embedding = new Vector(embedding),
            EmbeddingMetadata = new Vector(embeddingMetadata),
            Tags = tags,
            Confidence = confidence,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Title = title,
            Owner = memoryOwner,
            Archetype = archetype
        };

        const string sql =
            @"
            INSERT INTO memories (id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, owner_type, owner_id, archetype)
            VALUES (@id, @type, @content, @text, @source, @embedding, @embeddingMetadata, @tags, @confidence, @createdAt, @updatedAt, @title, @ownerType, @ownerId, @archetype)";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", memory.Id.Value);
        cmd.Parameters.AddWithValue("type", memory.Type);
        cmd.Parameters.AddWithValue("content", memory.Content);
        cmd.Parameters.AddWithValue("text", memory.Text);
        cmd.Parameters.AddWithValue("source", memory.Source);
        cmd.Parameters.AddWithValue("embedding", memory.Embedding);
        cmd.Parameters.AddWithValue("embeddingMetadata", memory.EmbeddingMetadata);
        cmd.Parameters.AddWithValue("tags", memory.Tags ?? []);
        cmd.Parameters.AddWithValue("confidence", (double)memory.Confidence);
        cmd.Parameters.AddWithValue("createdAt", memory.CreatedAt);
        cmd.Parameters.AddWithValue("updatedAt", memory.UpdatedAt);
        cmd.Parameters.AddWithValue("title", (object?)memory.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ownerType", (short)memory.Owner.Type);
        cmd.Parameters.AddWithValue("ownerId", memory.Owner.Id);
        cmd.Parameters.AddWithValue("archetype", (short)memory.Archetype);

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Optionally create a relationship
        if (relatedTo.HasValue && !string.IsNullOrWhiteSpace(relationshipType))
        {
            await CreateRelationship(memory.Id, relatedTo.Value, relationshipType, cancellationToken);
        }

        // Export to markdown file if enabled
        if (_markdownExportService is { IsEnabled: true })
        {
            try { await _markdownExportService.ExportMemoryAsync(memory, cancellationToken); }
            catch { /* Don't fail the store operation */ }
        }

        return memory;
    }

    public async Task<List<Memorizer.Models.Memory>> Search(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default
    )
    {
        var effectiveMinSimilarity = minSimilarity ?? SimilarityScore.DefaultThreshold;

        // Generate embedding for the query
        float[] queryEmbedding = await _embeddingService.Generate(
            query,
            cancellationToken
        );

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Always fetch up to 2x the requested limit for post-filtering/boosting
        int fetchLimit = limit * 2;

        // Build archetype filter - exclude System always, exclude Archived by default
        // ArchetypeEnum values: Document=0, Record=1, Archived=2, System=3, System=3
        // System memories are internal index entries and should never appear in user searches
        string archetypeFilter = includeArchived
            ? "AND archetype != 3"  // Exclude only System
            : "AND archetype IN (0, 1)";  // Only Document and Record

        string sql =
            $@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype, embedding <=> @embedding AS similarity
            FROM memories
            WHERE embedding <=> @embedding < @maxDistance
            {archetypeFilter}
            ORDER BY embedding <=> @embedding LIMIT @limit";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("maxDistance", effectiveMinSimilarity.ToDistance());
        cmd.Parameters.AddWithValue("limit", fetchLimit);

        List<Memorizer.Models.Memory> memories = [];
        List<MemoryId> memoryIds = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader, withSimilarity: true);
            memories.Add(memory);
            memoryIds.Add(memory.Id);
        }

        // Batch fetch relationships for all found memories
        if (memoryIds.Count > 0)
        {
            var relationships = await GetRelationshipsForMany(memoryIds, cancellationToken);
            var relLookup = relationships.GroupBy(r => r.FromMemoryId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var memory in memories)
            {
                if (relLookup.TryGetValue(memory.Id, out var rels))
                    memory.Relationships = rels;
                else
                    memory.Relationships = new List<MemoryRelationship>();
            }
        }

        // Tag normalization helper
        static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant();
        var normalizedFilterTags = filterTags?.Select(NormalizeTag).ToHashSet() ?? new HashSet<string>();
        const double tagBoost = 0.05; // 5% boost for tag match

        // Apply soft tag boost and sort
        var scored = memories.Select(m => {
            double score = m.Similarity.HasValue ? (double)m.Similarity.Value : 0.0;
            bool tagMatch = false;
            if (normalizedFilterTags.Count > 0 && m.Tags != null)
            {
                tagMatch = m.Tags.Select(NormalizeTag).Any(t => normalizedFilterTags.Contains(t));
                if (tagMatch) score += tagBoost; // Higher similarity = better match
            }
            return (Memory: m, Score: score, TagMatch: tagMatch);
        });

        // Sort by boosted score (higher is better), then by original similarity
        var sorted = scored.OrderByDescending(x => x.Score).ThenByDescending(x => x.Memory.Similarity.HasValue ? (double)x.Memory.Similarity.Value : 0.0).Take(limit).Select(x => x.Memory).ToList();
        return sorted;
    }

    public async Task<Memorizer.Models.Memory?> Get(
        MemoryId id,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql =
            @"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype
            FROM memories
            WHERE id = @id";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", id.Value);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader, withSimilarity: false);
            // Fetch relationships for this memory
            memory.Relationships = await GetRelationships(memory.Id, type: null, includeArchivedTargets: false, cancellationToken);
            return memory;
        }

        return null;
    }

    public async Task<bool> Delete(
        MemoryId id,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = "DELETE FROM memories WHERE id = @id";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", id.Value);

        int rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected > 0 && _markdownExportService is { IsEnabled: true })
        {
            try { await _markdownExportService.DeleteMemoryFileAsync(id, cancellationToken); }
            catch { /* Don't fail the delete operation */ }
        }

        return rowsAffected > 0;
    }

    public async Task<List<Memorizer.Models.Memory>> GetMany(IEnumerable<MemoryId> ids, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = @"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype
            FROM memories
            WHERE id = ANY(@ids)";
        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("ids", ids.Select(id => id.Value).ToArray());
        List<Memorizer.Models.Memory> memories = [];
        List<MemoryId> memoryIds = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader, withSimilarity: false);
            memories.Add(memory);
            memoryIds.Add(memory.Id);
        }
        // Batch fetch relationships for all found memories - now safe from infinite recursion!
        if (memoryIds.Count > 0)
        {
            var relationships = await GetRelationshipsForMany(memoryIds, cancellationToken);
            var relLookup = relationships.GroupBy(r => r.FromMemoryId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var memory in memories)
            {
                if (relLookup.TryGetValue(memory.Id, out var rels))
                    memory.Relationships = rels;
                else
                    memory.Relationships = new List<MemoryRelationship>();
            }
        }
        return memories;
    }

    public Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, CancellationToken cancellationToken = default)
    {
        return CreateRelationship(fromId, toId, type, (SimilarityScore?)null, cancellationToken);
    }

    public async Task<MemoryRelationship> CreateRelationship(MemoryId fromId, MemoryId toId, string type, SimilarityScore? score, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = @"
            INSERT INTO memory_relationships (id, from_memory_id, to_memory_id, type, created_at, score)
            VALUES (@id, @from, @to, @type, @createdAt, @score)";
        var rel = new MemoryRelationship
        {
            Id = RelationshipId.New(),
            FromMemoryId = fromId,
            ToMemoryId = toId,
            Type = type,
            CreatedAt = DateTime.UtcNow,
            Score = score
        };
        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", rel.Id.Value);
        cmd.Parameters.AddWithValue("from", rel.FromMemoryId.Value);
        cmd.Parameters.AddWithValue("to", rel.ToMemoryId.Value);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("createdAt", rel.CreatedAt);
        cmd.Parameters.AddWithValue("score", score.HasValue ? (double)score.Value : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rel;
    }

    public async Task<List<MemoryRelationship>> GetRelationships(MemoryId memoryId, string? type = null, bool includeArchivedTargets = false, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Single query with JOIN to get relationships and related memory titles/types - NO RECURSION!
        // Optionally filter out relationships pointing to archived memories
        string sql = $@"
            SELECT r.id, r.from_memory_id, r.to_memory_id, r.type, r.created_at,
                   m.title as related_title, m.type_legacy as related_type, r.score, m.archetype as related_archetype
            FROM memory_relationships r
            LEFT JOIN memories m ON r.to_memory_id = m.id
            WHERE r.from_memory_id = @id
            {(includeArchivedTargets ? "AND (m.archetype IS NULL OR m.archetype != 3)" : "AND (m.archetype IS NULL OR m.archetype IN (0, 1))")}";

        if (!string.IsNullOrEmpty(type))
            sql += " AND r.type = @type";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", memoryId.Value);
        if (!string.IsNullOrEmpty(type))
            cmd.Parameters.AddWithValue("type", type);

        List<MemoryRelationship> rels = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            // ArchetypeEnum values: Document=0, Record=1, Archived=2, System=3
            var relatedArchetype = reader.IsDBNull(8) ? (short?)null : reader.GetInt16(8);
            var rel = new MemoryRelationship
            {
                Id = (RelationshipId)reader.GetGuid(0),
                FromMemoryId = (MemoryId)reader.GetGuid(1),
                ToMemoryId = (MemoryId)reader.GetGuid(2),
                Type = reader.GetString(3),
                CreatedAt = reader.GetDateTime(4),
                RelatedMemoryTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                RelatedMemoryType = reader.IsDBNull(6) ? null : reader.GetString(6),
                Score = reader.IsDBNull(7) ? null : new SimilarityScore(reader.GetDouble(7)),
                TargetArchived = relatedArchetype == 2 // Archived = 2
            };
            rels.Add(rel);
        }

        return rels;
    }

    public async Task<List<SimilarMemory>> GetSimilarMemories(MemoryId memoryId, SimilarityScore? minSimilarity = null, int limit = 10, CancellationToken cancellationToken = default)
    {
        var effectiveMinSimilarity = minSimilarity ?? SimilarityScore.DefaultThreshold;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get the source memory's metadata embedding (title + tags)
        // Using metadata embeddings for better keyword-style similarity matching
        const string getEmbeddingSql = "SELECT embedding_metadata FROM memories WHERE id = @id";
        await using NpgsqlCommand getEmbeddingCmd = new(getEmbeddingSql, connection);
        getEmbeddingCmd.Parameters.AddWithValue("id", memoryId.Value);

        var embeddingResult = await getEmbeddingCmd.ExecuteScalarAsync(cancellationToken);
        if (embeddingResult is null || embeddingResult is DBNull)
        {
            return []; // No metadata embedding for this memory
        }

        var sourceEmbedding = (Vector)embeddingResult;

        // Convert similarity threshold to distance threshold
        // pgvector uses cosine distance where 0 = identical, 2 = opposite
        // similarity = 1 - distance, so distance = 1 - similarity
        double maxDistance = effectiveMinSimilarity.ToDistance();

        // Query for similar memories using metadata embeddings, excluding self, archived, and checking for existing relationships
        const string sql = @"
            SELECT m.id, m.title, m.type_legacy,
                   1 - (m.embedding_metadata <=> @embedding) AS similarity,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM memory_relationships r
                       WHERE (r.from_memory_id = @sourceId AND r.to_memory_id = m.id)
                          OR (r.from_memory_id = m.id AND r.to_memory_id = @sourceId)
                   ) THEN true ELSE false END AS has_relationship
            FROM memories m
            WHERE m.id != @sourceId
              AND m.embedding_metadata IS NOT NULL
              AND m.embedding_metadata <=> @embedding < @maxDistance
              AND m.archetype IN (0, 1)  -- Only Document and Record, exclude Archived and System
            ORDER BY m.embedding_metadata <=> @embedding
            LIMIT @limit";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("embedding", sourceEmbedding);
        cmd.Parameters.AddWithValue("sourceId", memoryId.Value);
        cmd.Parameters.AddWithValue("maxDistance", maxDistance);
        cmd.Parameters.AddWithValue("limit", limit);

        List<SimilarMemory> results = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var similar = new SimilarMemory
            {
                Id = (MemoryId)reader.GetGuid(0),
                Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Similarity = new SimilarityScore(reader.GetDouble(3)),
                HasExistingRelationship = reader.GetBoolean(4)
            };
            results.Add(similar);
        }

        return results;
    }

    public async Task<(List<Memorizer.Models.Memory> Memories, int TotalCount)> GetMemoriesPaginated(
        int page = 1,
        int pageSize = 20,
        string? memoryType = null,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var typeClause = !string.IsNullOrWhiteSpace(memoryType) ? " AND type_legacy = @memoryType" : "";

        // Get total count first
        // Exclude System (3) and Archived (2) memories from user-facing lists
        var countSql = $"SELECT COUNT(*) FROM memories WHERE archetype IN (0, 1){typeClause}";
        await using NpgsqlCommand countCmd = new(countSql, connection);
        if (!string.IsNullOrWhiteSpace(memoryType)) countCmd.Parameters.AddWithValue("memoryType", memoryType);
        var countResult = await countCmd.ExecuteScalarAsync(cancellationToken);
        var totalCount = countResult is null ? 0L : Convert.ToInt64(countResult);

        // Get paginated results
        var sql = $@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype
            FROM memories
            WHERE archetype IN (0, 1){typeClause}
            ORDER BY created_at DESC
            LIMIT @limit OFFSET @offset";

        await using NpgsqlCommand cmd = new(sql, connection);
        if (!string.IsNullOrWhiteSpace(memoryType)) cmd.Parameters.AddWithValue("memoryType", memoryType);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        List<Memorizer.Models.Memory> memories = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader, withSimilarity: false);
            // Fetch relationships for this memory
            memory.Relationships = await GetRelationships(memory.Id, type: null, includeArchivedTargets: false, cancellationToken);
            memories.Add(memory);
        }

        return (memories, (int)totalCount);
    }

    public async Task<Memorizer.Models.Memory?> UpdateMemory(
        MemoryId id,
        string type,
        string content,
        string source,
        string[]? tags,
        Confidence confidence,
        string? title = null,
        CancellationToken cancellationToken = default
    )
    {
        // First, get the existing memory to create a version snapshot
        var existingMemory = await Get(id, cancellationToken);
        if (existingMemory == null)
        {
            return null;
        }

        // Determine whether the incoming string is JSON or plain text
        JsonDocument document;
        string bodyText;

        try
        {
            document = JsonDocument.Parse(content);

            // Attempt to extract a sensible text body from common keys; fallback to full JSON text
            bodyText =
                document.RootElement.TryGetProperty("text", out var tElem) && tElem.ValueKind == JsonValueKind.String
                    ? tElem.GetString() ?? content
                : document.RootElement.TryGetProperty("fact", out var fElem) && fElem.ValueKind == JsonValueKind.String
                    ? fElem.GetString() ?? content
                : document.RootElement.TryGetProperty("observation", out var oElem) && oElem.ValueKind == JsonValueKind.String
                    ? oElem.GetString() ?? content
                : document.RootElement.TryGetProperty("content", out var cElem) && cElem.ValueKind == JsonValueKind.String
                    ? cElem.GetString() ?? content
                : content;
        }
        catch (JsonException)
        {
            // Not valid JSON – treat entire string as text; store an empty JSON object for backwards compatibility
            document = JsonDocument.Parse("{}");
            bodyText = content;
        }

        // Combine title and content for embedding if title is present
        string textToEmbed = bodyText;
        if (!string.IsNullOrWhiteSpace(title))
        {
            textToEmbed = title + " " + textToEmbed;
        }

        // Generate new embedding for updated content
        float[] embedding = await _embeddingService.Generate(
            textToEmbed,
            cancellationToken
        );

        // Generate new metadata embedding
        string metadataText = title ?? "";
        if (tags is { Length: > 0 })
        {
            metadataText += " " + string.Join(" ", tags);
        }

        float[] embeddingMetadata = await _embeddingService.Generate(
            metadataText,
            cancellationToken
        );

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Calculate new version number
            int newVersionNumber = existingMemory.CurrentVersion + 1;

            // Compute diff stats for the change event
            var diff = _diffService.ComputeDiff(existingMemory.Text, bodyText);
            var firstChange = diff.Lines.FirstOrDefault(l => l.Type != DiffLineType.Unchanged);
            var changeEvent = new ContentUpdatedEvent(
                existingMemory.Text,
                bodyText,
                diff.AddedCount,
                diff.RemovedCount,
                firstChange?.Text
            );

            // Create version snapshot of the OLD state before updating
            await CreateVersionSnapshot(
                connection,
                transaction,
                existingMemory,
                existingMemory.CurrentVersion,
                changeEvent,
                null,
                cancellationToken);

            // Update the memory with new content and increment version
            const string sql = @"
                UPDATE memories
                SET type_legacy = @type, content = @content, text = @text, source = @source,
                    embedding = @embedding, embedding_metadata = @embeddingMetadata, tags = @tags, confidence = @confidence,
                    updated_at = @updatedAt, title = @title, current_version = @currentVersion
                WHERE id = @id";

            await using NpgsqlCommand cmd = new(sql, connection, transaction);
            cmd.Parameters.AddWithValue("id", id.Value);
            cmd.Parameters.AddWithValue("type", type);
            cmd.Parameters.AddWithValue("content", document);
            cmd.Parameters.AddWithValue("text", bodyText); // Store original content, not the embedding text
            cmd.Parameters.AddWithValue("source", source);
            cmd.Parameters.AddWithValue("embedding", new Vector(embedding));
            cmd.Parameters.AddWithValue("embeddingMetadata", new Vector(embeddingMetadata));
            cmd.Parameters.AddWithValue("tags", tags ?? []);
            cmd.Parameters.AddWithValue("confidence", (double)confidence);
            cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("title", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("currentVersion", newVersionNumber);

            int rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

            // Prune old versions if we exceed the limit
            await PruneOldVersionsIfNeeded(id.Value, connection, transaction, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            // Return the updated memory if successful
            if (rowsAffected > 0)
            {
                var updatedMemory = await Get(id, cancellationToken);
                if (updatedMemory != null && _markdownExportService is { IsEnabled: true })
                {
                    try { await _markdownExportService.ExportMemoryAsync(updatedMemory, cancellationToken); }
                    catch { /* Don't fail the update operation */ }
                }
                return updatedMemory;
            }

            return null;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // Helper to batch fetch relationships for many memory IDs
    // By default, excludes relationships pointing to archived memories
    private async Task<List<MemoryRelationship>> GetRelationshipsForMany(IEnumerable<MemoryId> memoryIds, CancellationToken cancellationToken, bool includeArchivedTargets = false)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Single query to get relationships with related memory titles/types - NO RECURSION!
        // Optionally filter out relationships pointing to archived memories
        string sql = $@"
            SELECT r.id, r.from_memory_id, r.to_memory_id, r.type, r.created_at,
                   m.title as related_title, m.type_legacy as related_type, m.archetype as related_archetype
            FROM memory_relationships r
            LEFT JOIN memories m ON r.to_memory_id = m.id
            WHERE r.from_memory_id = ANY(@ids)
            {(includeArchivedTargets ? "AND (m.archetype IS NULL OR m.archetype != 3)" : "AND (m.archetype IS NULL OR m.archetype IN (0, 1))")}";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("ids", memoryIds.Select(id => id.Value).ToArray());

        List<MemoryRelationship> rels = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            // ArchetypeEnum values: Document=0, Record=1, Archived=2, System=3
            var relatedArchetype = reader.IsDBNull(7) ? (short?)null : reader.GetInt16(7);
            var rel = new MemoryRelationship
            {
                Id = (RelationshipId)reader.GetGuid(0),
                FromMemoryId = (MemoryId)reader.GetGuid(1),
                ToMemoryId = (MemoryId)reader.GetGuid(2),
                Type = reader.GetString(3),
                CreatedAt = reader.GetDateTime(4),
                RelatedMemoryTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                RelatedMemoryType = reader.IsDBNull(6) ? null : reader.GetString(6),
                TargetArchived = relatedArchetype == 2 // Archived = 2
            };
            rels.Add(rel);
        }

        return rels;
    }

    // Title generation support
    public async Task<List<Memorizer.Models.Memory>> GetMemoriesWithoutTitles(
        int limit = 50,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype
            FROM memories
            WHERE title IS NULL OR title = ''
            ORDER BY created_at DESC
            LIMIT @limit";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("limit", limit);

        List<Memorizer.Models.Memory> memories = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader, withSimilarity: false);
            memories.Add(memory);
        }

        return memories;
    }

    public async Task UpdateMemoryTitle(
        MemoryId id,
        string title,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            UPDATE memories 
            SET title = @title
            WHERE id = @id";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("title", title);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateMemoryOwner(
        MemoryId id,
        MemoryOwner owner,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            UPDATE memories
            SET owner_type = @ownerType, owner_id = @ownerId
            WHERE id = @id";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("ownerType", (short)owner.Type);
        cmd.Parameters.AddWithValue("ownerId", owner.Id);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // Dual embedding comparison methods for PoC
    public async Task<List<Memorizer.Models.Memory>> SearchWithFullEmbedding(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default
    )
    {
        var effectiveMinSimilarity = minSimilarity ?? SimilarityScore.DefaultThreshold;

        // Generate embedding for the query
        float[] queryEmbedding = await _embeddingService.Generate(
            query,
            cancellationToken
        );

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Always fetch up to 2x the requested limit for post-filtering/boosting
        int fetchLimit = limit * 2;

        // Build archetype filter - exclude System always, exclude Archived by default
        // ArchetypeEnum values: Document=0, Record=1, Archived=2, System=3, System=3
        // System memories are internal index entries and should never appear in user searches
        string archetypeFilter = includeArchived
            ? "AND archetype != 3"  // Exclude only System
            : "AND archetype IN (0, 1)";  // Only Document and Record

        string sql =
            $@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype, embedding <=> @embedding AS similarity
            FROM memories
            WHERE embedding <=> @embedding < @maxDistance
            {archetypeFilter}
            ORDER BY embedding <=> @embedding LIMIT @limit";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("maxDistance", effectiveMinSimilarity.ToDistance());
        cmd.Parameters.AddWithValue("limit", fetchLimit);

        List<Memorizer.Models.Memory> memories = [];
        List<MemoryId> memoryIds = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader, withSimilarity: true);
            memories.Add(memory);
            memoryIds.Add(memory.Id);
        }

        // Batch fetch relationships for all found memories
        if (memoryIds.Count > 0)
        {
            var relationships = await GetRelationshipsForMany(memoryIds, cancellationToken);
            var relLookup = relationships.GroupBy(r => r.FromMemoryId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var memory in memories)
            {
                if (relLookup.TryGetValue(memory.Id, out var rels))
                    memory.Relationships = rels;
                else
                    memory.Relationships = new List<MemoryRelationship>();
            }
        }

        // Tag normalization helper
        static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant();
        var normalizedFilterTags = filterTags?.Select(NormalizeTag).ToHashSet() ?? new HashSet<string>();
        const double tagBoost = 0.05; // 5% boost for tag match

        // Apply soft tag boost and sort
        var scored = memories.Select(m => {
            double score = m.Similarity.HasValue ? (double)m.Similarity.Value : 0.0;
            bool tagMatch = false;
            if (normalizedFilterTags.Count > 0 && m.Tags != null)
            {
                tagMatch = m.Tags.Select(NormalizeTag).Any(t => normalizedFilterTags.Contains(t));
                if (tagMatch) score += tagBoost; // Higher similarity = better match
            }
            return (Memory: m, Score: score, TagMatch: tagMatch);
        });

        // Sort by boosted score (higher is better), then by original similarity
        var sorted = scored.OrderByDescending(x => x.Score).ThenByDescending(x => x.Memory.Similarity.HasValue ? (double)x.Memory.Similarity.Value : 0.0).Take(limit).Select(x => x.Memory).ToList();
        return sorted;
    }

    public async Task<List<Memorizer.Models.Memory>> SearchWithMetadataEmbedding(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        ProjectId? projectId = null,
        bool includeUnassigned = false,
        bool includeArchived = false,
        bool includeSystem = false,
        CancellationToken cancellationToken = default
    )
    {
        var effectiveMinSimilarity = minSimilarity ?? SimilarityScore.DefaultThreshold;

        // Generate embedding for the query
        float[] queryEmbedding = await _embeddingService.Generate(
            query,
            cancellationToken
        );

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // IVFFlat uses approximate nearest-neighbor search. The default probes=1 with lists=100
        // means only 1% of the index is scanned, which causes missed results on small datasets.
        // Setting probes to sqrt(lists)=10 gives good recall without meaningful overhead.
        await using (var probeCmd = new NpgsqlCommand("SET LOCAL ivfflat.probes = 10", connection))
            await probeCmd.ExecuteNonQueryAsync(cancellationToken);

        // Always fetch up to 2x the requested limit for post-filtering/boosting
        int fetchLimit = limit * 2;

        // Build owner filter clause
        string ownerFilter = "";
        if (projectId.HasValue)
        {
            if (includeUnassigned)
            {
                // Include both project-owned and unfiled memories
                ownerFilter = @"AND ((owner_type = 1 AND owner_id = @projectId)
                               OR (owner_type = 0 AND owner_id = '00000000-0000-0000-0000-000000000000'))";
            }
            else
            {
                // Only project-owned memories
                ownerFilter = "AND owner_type = 1 AND owner_id = @projectId";
            }
        }
        // If no projectId specified, search across all memories (original behavior)

        // Build archetype filter
        // ArchetypeEnum values: Document=0, Record=1, Archived=2, System=3
        // By default, exclude both Archived and System memories
        string archetypeFilter = (includeArchived, includeSystem) switch
        {
            (false, false) => "AND archetype IN (0, 1)",      // Only Document and Record
            (true, false) => "AND archetype IN (0, 1, 2)",    // Document, Record, Archived
            (false, true) => "AND archetype IN (0, 1, 3)",    // Document, Record, System
            (true, true) => ""                                 // All archetypes
        };

        string sql =
            $@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype, embedding_metadata <=> @embedding AS similarity
            FROM memories
            WHERE embedding_metadata <=> @embedding < @maxDistance
            {ownerFilter}
            {archetypeFilter}
            ORDER BY embedding_metadata <=> @embedding LIMIT @limit";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("maxDistance", effectiveMinSimilarity.ToDistance());
        cmd.Parameters.AddWithValue("limit", fetchLimit);

        if (projectId.HasValue)
        {
            cmd.Parameters.AddWithValue("projectId", projectId.Value.Value);
        }

        List<Memorizer.Models.Memory> memories = [];
        List<MemoryId> memoryIds = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader, withSimilarity: true);
            memories.Add(memory);
            memoryIds.Add(memory.Id);
        }

        // Batch fetch relationships for all found memories
        if (memoryIds.Count > 0)
        {
            var relationships = await GetRelationshipsForMany(memoryIds, cancellationToken);
            var relLookup = relationships.GroupBy(r => r.FromMemoryId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var memory in memories)
            {
                if (relLookup.TryGetValue(memory.Id, out var rels))
                    memory.Relationships = rels;
                else
                    memory.Relationships = new List<MemoryRelationship>();
            }
        }

        // Tag normalization helper
        static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant();
        var normalizedFilterTags = filterTags?.Select(NormalizeTag).ToHashSet() ?? new HashSet<string>();
        const double tagBoost = 0.05; // 5% boost for tag match

        // Apply soft tag boost and sort
        var scored = memories.Select(m => {
            double score = m.Similarity.HasValue ? (double)m.Similarity.Value : 0.0;
            bool tagMatch = false;
            if (normalizedFilterTags.Count > 0 && m.Tags != null)
            {
                tagMatch = m.Tags.Select(NormalizeTag).Any(t => normalizedFilterTags.Contains(t));
                if (tagMatch) score += tagBoost; // Higher similarity = better match
            }
            return (Memory: m, Score: score, TagMatch: tagMatch);
        });

        // Sort by boosted score (higher is better), then by original similarity
        var sorted = scored.OrderByDescending(x => x.Score).ThenByDescending(x => x.Memory.Similarity.HasValue ? (double)x.Memory.Similarity.Value : 0.0).Take(limit).Select(x => x.Memory).ToList();
        return sorted;
    }

    public async Task<(List<Memorizer.Models.Memory> FullResults, List<Memorizer.Models.Memory> MetadataResults)> CompareSearchMethods(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    )
    {
        var fullEmbeddingResults = await SearchWithFullEmbedding(query, limit, minSimilarity, filterTags, includeArchived: false, cancellationToken);
        var metadataEmbeddingResults = await SearchWithMetadataEmbedding(query, limit, minSimilarity, filterTags, projectId: null, includeUnassigned: false, includeArchived: false, includeSystem: false, cancellationToken);
        return (fullEmbeddingResults, metadataEmbeddingResults);
    }

    private static string BuildOwnerFilter(ProjectId? projectId, bool includeUnassigned)
    {
        if (!projectId.HasValue) return "";

        if (includeUnassigned)
        {
            return @"AND ((owner_type = 1 AND owner_id = @projectId)
                   OR (owner_type = 0 AND owner_id = '00000000-0000-0000-0000-000000000000'))";
        }

        return "AND owner_type = 1 AND owner_id = @projectId";
    }

    private static string BuildArchetypeFilter(bool includeArchived, bool includeSystem)
    {
        return (includeArchived, includeSystem) switch
        {
            (false, false) => "AND archetype IN (0, 1)",
            (true, false) => "AND archetype IN (0, 1, 2)",
            (false, true) => "AND archetype IN (0, 1, 3)",
            (true, true) => ""
        };
    }

    /// <summary>
    /// Builds a tsquery string using AND with prefix matching for each term.
    /// This handles stemming mismatches (e.g., "postgres" matching "postgresql")
    /// while keeping AND semantics so all terms must be present.
    /// </summary>
    private static string BuildPrefixTsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()))
            .Where(t => t.Length > 1)
            .ToArray();

        if (terms.Length == 0)
            return query; // fallback to raw query

        return string.Join(" & ", terms.Select(t => $"{t}:*"));
    }

    public async Task<List<Memorizer.Models.Memory>> HybridSearch(
        string query,
        int limit = 10,
        SimilarityScore? minSimilarity = null,
        string[]? filterTags = null,
        ProjectId? projectId = null,
        bool includeUnassigned = false,
        bool includeArchived = false,
        bool includeSystem = false,
        CancellationToken cancellationToken = default
    )
    {
        // Generate embedding for the query
        float[] queryEmbedding = await _embeddingService.Generate(query, cancellationToken);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        int fetchLimit = Math.Max(limit * 3, 30);
        string ownerFilter = BuildOwnerFilter(projectId, includeUnassigned);
        string archetypeFilter = BuildArchetypeFilter(includeArchived, includeSystem);

        // Leg 1: Vector search (metadata embedding, no hard distance threshold)
        string vectorSql = $@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata,
                   tags, confidence, created_at, updated_at, title, current_version,
                   owner_type, owner_id, archetype,
                   embedding_metadata <=> @embedding AS similarity
            FROM memories
            WHERE embedding_metadata IS NOT NULL
            {ownerFilter}
            {archetypeFilter}
            ORDER BY embedding_metadata <=> @embedding
            LIMIT @fetchLimit";

        var vectorResults = new List<(Memorizer.Models.Memory Memory, double Distance)>();
        await using (var cmd = new NpgsqlCommand(vectorSql, connection))
        {
            cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
            cmd.Parameters.AddWithValue("fetchLimit", fetchLimit);
            if (projectId.HasValue)
                cmd.Parameters.AddWithValue("projectId", projectId.Value.Value);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var memory = ReadMemoryFromReader(reader, withSimilarity: false);
                var distance = reader.GetDouble(16);
                vectorResults.Add((memory, distance));
            }
        }

        // Leg 2: Full-text search (AND with prefix matching for better stemming coverage)
        string tsquery = BuildPrefixTsQuery(query);
        string ftsSql = $@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata,
                   tags, confidence, created_at, updated_at, title, current_version,
                   owner_type, owner_id, archetype,
                   ts_rank_cd(search_vector, to_tsquery('english', @tsquery)) AS fts_rank
            FROM memories
            WHERE search_vector @@ to_tsquery('english', @tsquery)
            {ownerFilter}
            {archetypeFilter}
            ORDER BY fts_rank DESC
            LIMIT @fetchLimit";

        var ftsResults = new List<(Memorizer.Models.Memory Memory, double FtsRank)>();
        await using (var cmd = new NpgsqlCommand(ftsSql, connection))
        {
            cmd.Parameters.AddWithValue("tsquery", tsquery);
            cmd.Parameters.AddWithValue("fetchLimit", fetchLimit);
            if (projectId.HasValue)
                cmd.Parameters.AddWithValue("projectId", projectId.Value.Value);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var memory = ReadMemoryFromReader(reader, withSimilarity: false);
                var ftsRank = reader.GetDouble(16);
                ftsResults.Add((memory, ftsRank));
            }
        }

        // RRF Fusion (k=60)
        const int k = 60;

        // Adaptive weighting: short queries favor FTS, longer queries weight equally
        int wordCount = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        double ftsWeight = wordCount <= 2 ? 1.5 : 1.0;
        double vectorWeight = 1.0;

        var rrfScores = new Dictionary<MemoryId, (double Score, Memorizer.Models.Memory Memory)>();

        // Score vector results
        for (int i = 0; i < vectorResults.Count; i++)
        {
            var (memory, _) = vectorResults[i];
            int rank = i + 1; // 1-based rank
            double score = vectorWeight / (k + rank);
            rrfScores[memory.Id] = (score, memory);
        }

        // Score FTS results and merge
        for (int i = 0; i < ftsResults.Count; i++)
        {
            var (memory, _) = ftsResults[i];
            int rank = i + 1;
            double score = ftsWeight / (k + rank);

            if (rrfScores.TryGetValue(memory.Id, out var existing))
            {
                rrfScores[memory.Id] = (existing.Score + score, existing.Memory);
            }
            else
            {
                rrfScores[memory.Id] = (score, memory);
            }
        }

        // Tag normalization and boosting
        static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant();
        var normalizedFilterTags = filterTags?.Select(NormalizeTag).ToHashSet() ?? new HashSet<string>();
        const double tagBoostFactor = 1.1; // 10% boost for tag match

        var ranked = rrfScores.Values
            .Select(entry =>
            {
                double score = entry.Score;
                if (normalizedFilterTags.Count > 0 && entry.Memory.Tags != null)
                {
                    bool tagMatch = entry.Memory.Tags.Select(NormalizeTag).Any(t => normalizedFilterTags.Contains(t));
                    if (tagMatch) score *= tagBoostFactor;
                }
                return (entry.Memory, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .ToList();

        // Set similarity scores on the final results from vector leg distances
        var vectorDistanceLookup = vectorResults.ToDictionary(v => v.Memory.Id, v => v.Distance);
        var memories = new List<Memorizer.Models.Memory>();
        var memoryIds = new List<MemoryId>();

        foreach (var (memory, _) in ranked)
        {
            if (vectorDistanceLookup.TryGetValue(memory.Id, out var distance))
            {
                memory.Similarity = SimilarityScore.FromDistance(distance);
            }
            memories.Add(memory);
            memoryIds.Add(memory.Id);
        }

        // Batch fetch relationships for all found memories
        if (memoryIds.Count > 0)
        {
            var relationships = await GetRelationshipsForMany(memoryIds, cancellationToken);
            var relLookup = relationships.GroupBy(r => r.FromMemoryId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var memory in memories)
            {
                if (relLookup.TryGetValue(memory.Id, out var rels))
                    memory.Relationships = rels;
                else
                    memory.Relationships = new List<MemoryRelationship>();
            }
        }

        return memories;
    }

    // Metadata embedding support
    public async Task<int> CountMemoriesWithoutMetadataEmbeddings(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT COUNT(*) FROM memories WHERE embedding_metadata IS NULL AND archetype IN (0, 1)";
        
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task<List<Memorizer.Models.Memory>> GetMemoriesWithoutMetadataEmbeddings(int limit, bool includeExisting = false, CancellationToken cancellationToken = default)
    {
        var whereClause = includeExisting
            ? "WHERE archetype IN (0, 1)"
            : "WHERE embedding_metadata IS NULL AND archetype IN (0, 1)";
        var sql = $@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype
            FROM memories
            {whereClause}
            ORDER BY created_at ASC
            LIMIT @limit";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);

        var memories = new List<Memorizer.Models.Memory>();
        var memoryIds = new List<MemoryId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader, withSimilarity: false);
            memories.Add(memory);
            memoryIds.Add(memory.Id);
        }

        // Batch fetch relationships for all found memories
        if (memoryIds.Count > 0)
        {
            var relationships = await GetRelationshipsForMany(memoryIds, cancellationToken);
            var relLookup = relationships.GroupBy(r => r.FromMemoryId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var memory in memories)
            {
                if (relLookup.TryGetValue(memory.Id, out var rels))
                    memory.Relationships = rels;
            }
        }

        return memories;
    }

    public async Task UpdateMemoryMetadataEmbedding(MemoryId memoryId, Vector embedding, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE memories
            SET embedding_metadata = @embedding, updated_at = NOW()
            WHERE id = @id";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", memoryId.Value);
        command.Parameters.AddWithValue("embedding", embedding);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateMemoryEmbeddings(MemoryId memoryId, Vector contentEmbedding, Vector metadataEmbedding, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE memories
            SET embedding = @contentEmbedding, embedding_metadata = @metadataEmbedding, updated_at = NOW()
            WHERE id = @id";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", memoryId.Value);
        command.Parameters.AddWithValue("contentEmbedding", contentEmbedding);
        command.Parameters.AddWithValue("metadataEmbedding", metadataEmbedding);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Get distinct memory types
    public async Task<List<string>> GetDistinctMemoryTypes(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT DISTINCT type_legacy FROM memories";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task<List<string>> GetDistinctTagsAsync(MemoryOwner? owner = null, CancellationToken cancellationToken = default)
    {
        var ownerClause = "";
        if (owner.HasValue)
        {
            ownerClause = " AND owner_type = @ownerType AND owner_id = @ownerId";
        }

        var sql = $@"
            SELECT DISTINCT tag
            FROM memories, unnest(tags) AS tag
            WHERE archetype IN (0, 1){ownerClause}
            ORDER BY tag";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        if (owner.HasValue)
        {
            command.Parameters.AddWithValue("ownerType", (short)owner.Value.Type);
            command.Parameters.AddWithValue("ownerId", owner.Value.Id);
        }

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task<List<MemoryOwner>> GetDistinctOwnersAsync(string[]? tags = null, string? memoryType = null, CancellationToken cancellationToken = default)
    {
        var clauses = new List<string> { "archetype IN (0, 1)" };

        var validTags = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
        for (var i = 0; i < validTags.Length; i++)
        {
            clauses.Add($"EXISTS (SELECT 1 FROM unnest(tags) t WHERE lower(t) = lower(@tag{i}))");
        }

        if (!string.IsNullOrWhiteSpace(memoryType))
        {
            clauses.Add("type_legacy = @memoryType");
        }

        var where = string.Join(" AND ", clauses);
        var sql = $"SELECT DISTINCT owner_type, owner_id FROM memories WHERE {where}";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        for (var i = 0; i < validTags.Length; i++)
            command.Parameters.AddWithValue($"tag{i}", validTags[i]);
        if (!string.IsNullOrWhiteSpace(memoryType))
            command.Parameters.AddWithValue("memoryType", memoryType);

        var result = new List<MemoryOwner>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var ownerType = (OwnerTypeEnum)reader.GetInt16(0);
            var ownerId = reader.GetGuid(1);
            result.Add(new MemoryOwner { Type = ownerType, Id = ownerId });
        }

        return result;
    }

    // ==================== VERSIONING SUPPORT ====================

    public async Task<List<MemoryEvent>> GetEvents(MemoryId memoryId, int? limit = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT event_id, memory_id, version_number, event_type, event_data, timestamp, changed_by
            FROM memory_events
            WHERE memory_id = @memoryId
            ORDER BY version_number DESC, timestamp DESC";

        if (limit.HasValue)
            sql += " LIMIT @limit";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("memoryId", memoryId.Value);
        if (limit.HasValue)
            cmd.Parameters.AddWithValue("limit", limit.Value);

        var events = new List<MemoryEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadEventFromReader(reader));
        }

        return events;
    }

    public async Task<List<MemoryVersion>> GetVersionHistory(MemoryId memoryId, int? limit = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT version_id, memory_id, version_number, type, content, text, source, tags, confidence, title, relationship_ids, created_at, versioned_at
            FROM memory_versions
            WHERE memory_id = @memoryId
            ORDER BY version_number DESC";

        if (limit.HasValue)
            sql += " LIMIT @limit";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("memoryId", memoryId.Value);
        if (limit.HasValue)
            cmd.Parameters.AddWithValue("limit", limit.Value);

        var versions = new List<MemoryVersion>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(ReadVersionFromReader(reader));
        }

        // Optionally load events for each version
        if (versions.Count > 0)
        {
            var events = await GetEvents(memoryId, null, cancellationToken);
            var eventsByVersion = events.GroupBy(e => (int)e.VersionNumber).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var version in versions)
            {
                if (eventsByVersion.TryGetValue((int)version.VersionNumber, out var versionEvents))
                    version.Events = versionEvents;
            }
        }

        return versions;
    }

    public async Task<MemoryVersion?> GetVersion(MemoryId memoryId, VersionNumber versionNumber, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT version_id, memory_id, version_number, type, content, text, source, tags, confidence, title, relationship_ids, created_at, versioned_at
            FROM memory_versions
            WHERE memory_id = @memoryId AND version_number = @versionNumber";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("memoryId", memoryId.Value);
        cmd.Parameters.AddWithValue("versionNumber", (int)versionNumber);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var version = ReadVersionFromReader(reader);

            // Load events for this version
            var events = await GetEvents(memoryId, null, cancellationToken);
            version.Events = events.Where(e => e.VersionNumber == versionNumber).ToList();

            return version;
        }

        return null;
    }

    public async Task<Memorizer.Models.Memory?> RevertToVersion(MemoryId memoryId, VersionNumber versionNumber, string? changedBy = null, CancellationToken cancellationToken = default)
    {
        // Get the target version snapshot
        var targetVersion = await GetVersion(memoryId, versionNumber, cancellationToken);
        if (targetVersion == null)
            return null;

        // Get current memory to determine new version number
        var currentMemory = await Get(memoryId, cancellationToken);
        if (currentMemory == null)
            return null;

        var newVersionNumber = (int)currentMemory.CurrentVersion + 1;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Generate new embeddings for the restored content
            string textToEmbed = targetVersion.Text;
            if (!string.IsNullOrWhiteSpace(targetVersion.Title))
                textToEmbed = targetVersion.Title + " " + textToEmbed;

            float[] embedding = await _embeddingService.Generate(textToEmbed, cancellationToken);

            string metadataText = targetVersion.Title ?? "";
            if (targetVersion.Tags is { Length: > 0 })
                metadataText += " " + string.Join(" ", targetVersion.Tags);

            float[] embeddingMetadata = await _embeddingService.Generate(metadataText, cancellationToken);

            // Record revert event
            var revertEvent = new MemoryRevertedEvent((int)versionNumber, (int)currentMemory.CurrentVersion);
            var (eventType, eventData) = revertEvent.Serialize();

            const string eventSql = @"
                INSERT INTO memory_events (memory_id, version_number, event_type, event_data, changed_by)
                VALUES (@memoryId, @versionNumber, @eventType, @eventData, @changedBy)";

            await using (var eventCmd = new NpgsqlCommand(eventSql, connection, transaction))
            {
                eventCmd.Parameters.AddWithValue("memoryId", memoryId.Value);
                eventCmd.Parameters.AddWithValue("versionNumber", newVersionNumber);
                eventCmd.Parameters.AddWithValue("eventType", eventType);
                eventCmd.Parameters.AddWithValue("eventData", eventData);
                eventCmd.Parameters.AddWithValue("changedBy", (object?)changedBy ?? DBNull.Value);
                await eventCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Create snapshot of CURRENT state before reverting (for history)
            // This preserves the state at currentMemory.CurrentVersion so it can be reverted back to
            var relationshipIds = currentMemory.Relationships?.Select(r => r.Id.Value).ToArray() ?? Array.Empty<Guid>();

            const string snapshotSql = @"
                INSERT INTO memory_versions (memory_id, version_number, type, content, text, source, tags, confidence, title, relationship_ids, created_at)
                VALUES (@memoryId, @versionNumber, @type, @content, @text, @source, @tags, @confidence, @title, @relationshipIds, @createdAt)
                ON CONFLICT (memory_id, version_number) DO NOTHING";

            await using (var snapshotCmd = new NpgsqlCommand(snapshotSql, connection, transaction))
            {
                snapshotCmd.Parameters.AddWithValue("memoryId", memoryId.Value);
                // Use CURRENT version number - this snapshots the state BEFORE the revert
                snapshotCmd.Parameters.AddWithValue("versionNumber", (int)currentMemory.CurrentVersion);
                // Use CURRENT memory content, not target version content
                snapshotCmd.Parameters.AddWithValue("type", currentMemory.Type);
                snapshotCmd.Parameters.AddWithValue("content", currentMemory.Content);
                snapshotCmd.Parameters.AddWithValue("text", currentMemory.Text);
                snapshotCmd.Parameters.AddWithValue("source", currentMemory.Source);
                snapshotCmd.Parameters.AddWithValue("tags", currentMemory.Tags ?? Array.Empty<string>());
                snapshotCmd.Parameters.AddWithValue("confidence", (double)currentMemory.Confidence);
                snapshotCmd.Parameters.AddWithValue("title", (object?)currentMemory.Title ?? DBNull.Value);
                snapshotCmd.Parameters.AddWithValue("relationshipIds", relationshipIds);
                snapshotCmd.Parameters.AddWithValue("createdAt", currentMemory.CreatedAt);
                await snapshotCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Update the memories table with restored content
            const string updateSql = @"
                UPDATE memories
                SET type_legacy = @type, content = @content, text = @text, source = @source,
                    tags = @tags, confidence = @confidence, title = @title,
                    embedding = @embedding, embedding_metadata = @embeddingMetadata,
                    current_version = @currentVersion, updated_at = NOW()
                WHERE id = @id";

            await using (var updateCmd = new NpgsqlCommand(updateSql, connection, transaction))
            {
                updateCmd.Parameters.AddWithValue("id", memoryId.Value);
                updateCmd.Parameters.AddWithValue("type", targetVersion.Type);
                updateCmd.Parameters.AddWithValue("content", targetVersion.Content);
                updateCmd.Parameters.AddWithValue("text", targetVersion.Text);
                updateCmd.Parameters.AddWithValue("source", targetVersion.Source);
                updateCmd.Parameters.AddWithValue("tags", targetVersion.Tags ?? Array.Empty<string>());
                updateCmd.Parameters.AddWithValue("confidence", (double)targetVersion.Confidence);
                updateCmd.Parameters.AddWithValue("title", (object?)targetVersion.Title ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("embedding", new Vector(embedding));
                updateCmd.Parameters.AddWithValue("embeddingMetadata", new Vector(embeddingMetadata));
                updateCmd.Parameters.AddWithValue("currentVersion", newVersionNumber);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Prune old versions if we exceed the limit
            await PruneOldVersionsIfNeeded(memoryId.Value, connection, transaction, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            // Return the updated memory
            return await Get(memoryId, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> PurgeVersionsKeepingLatest(MemoryId memoryId, int versionsToKeep, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            WITH versions_to_delete AS (
                SELECT version_id
                FROM memory_versions
                WHERE memory_id = @memoryId
                ORDER BY version_number DESC
                OFFSET @keepCount
            )
            DELETE FROM memory_versions
            WHERE version_id IN (SELECT version_id FROM versions_to_delete)";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("memoryId", memoryId.Value);
        cmd.Parameters.AddWithValue("keepCount", versionsToKeep);

        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PurgeVersionsOlderThan(DateTime cutoffDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Delete old events
            const string eventSql = @"
                DELETE FROM memory_events
                WHERE timestamp < @cutoff";

            await using (var eventCmd = new NpgsqlCommand(eventSql, connection, transaction))
            {
                eventCmd.Parameters.AddWithValue("cutoff", cutoffDate);
                await eventCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Delete old versions but keep at least the latest version for each memory
            const string versionSql = @"
                DELETE FROM memory_versions
                WHERE versioned_at < @cutoff
                AND version_number < (
                    SELECT MAX(version_number)
                    FROM memory_versions mv2
                    WHERE mv2.memory_id = memory_versions.memory_id
                )";

            await using var versionCmd = new NpgsqlCommand(versionSql, connection, transaction);
            versionCmd.Parameters.AddWithValue("cutoff", cutoffDate);

            var deleted = await versionCmd.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<VersionStats> GetVersionStats(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT
                (SELECT COUNT(*) FROM memories WHERE archetype IN (0, 1)) as total_memories,
                (SELECT COUNT(*) FROM memory_versions) as total_versions,
                (SELECT COUNT(*) FROM memory_events) as total_events,
                (SELECT AVG(version_count) FROM (
                    SELECT COUNT(*) as version_count FROM memory_versions GROUP BY memory_id
                ) sub) as avg_versions,
                (SELECT COUNT(*) FROM (
                    SELECT memory_id FROM memory_versions GROUP BY memory_id HAVING COUNT(*) > 1
                ) sub) as memories_with_multiple_versions,
                (SELECT MIN(versioned_at) FROM memory_versions) as oldest_version,
                (SELECT MAX(versioned_at) FROM memory_versions) as newest_version,
                (SELECT pg_total_relation_size('memory_versions') + pg_total_relation_size('memory_events')) as storage_bytes";

        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            return new VersionStats
            {
                TotalMemories = reader.GetInt32(0),
                TotalVersions = reader.GetInt32(1),
                TotalEvents = reader.GetInt32(2),
                AverageVersionsPerMemory = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                MemoriesWithMultipleVersions = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetInt64(4)),
                OldestVersion = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                NewestVersion = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                EstimatedStorageBytes = reader.IsDBNull(7) ? 0 : reader.GetInt64(7)
            };
        }

        return new VersionStats();
    }

    // Helper method to create a version snapshot and event when updating a memory
    internal async Task CreateVersionSnapshot(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Memorizer.Models.Memory memory,
        int newVersionNumber,
        MemoryChangeEvent changeEvent,
        string? changedBy,
        CancellationToken cancellationToken)
    {
        // Get current relationship IDs
        var relationshipIds = memory.Relationships?.Select(r => r.Id.Value).ToArray() ?? Array.Empty<Guid>();

        // Serialize the strongly-typed event
        var (eventType, eventDataJson) = changeEvent.Serialize();

        const string eventSql = @"
            INSERT INTO memory_events (memory_id, version_number, event_type, event_data, changed_by)
            VALUES (@memoryId, @versionNumber, @eventType, @eventData, @changedBy)";

        await using (var eventCmd = new NpgsqlCommand(eventSql, connection, transaction))
        {
            eventCmd.Parameters.AddWithValue("memoryId", memory.Id.Value);
            eventCmd.Parameters.AddWithValue("versionNumber", newVersionNumber);
            eventCmd.Parameters.AddWithValue("eventType", eventType);
            eventCmd.Parameters.AddWithValue("eventData", eventDataJson);
            eventCmd.Parameters.AddWithValue("changedBy", (object?)changedBy ?? DBNull.Value);
            await eventCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Create snapshot (use ON CONFLICT DO NOTHING to handle pre-existing snapshots from migration)
        const string snapshotSql = @"
            INSERT INTO memory_versions (memory_id, version_number, type, content, text, source, tags, confidence, title, relationship_ids, created_at)
            VALUES (@memoryId, @versionNumber, @type, @content, @text, @source, @tags, @confidence, @title, @relationshipIds, @createdAt)
            ON CONFLICT (memory_id, version_number) DO NOTHING";

        await using var snapshotCmd = new NpgsqlCommand(snapshotSql, connection, transaction);
        snapshotCmd.Parameters.AddWithValue("memoryId", memory.Id.Value);
        snapshotCmd.Parameters.AddWithValue("versionNumber", newVersionNumber);
        snapshotCmd.Parameters.AddWithValue("type", memory.Type);
        snapshotCmd.Parameters.AddWithValue("content", memory.Content);
        snapshotCmd.Parameters.AddWithValue("text", memory.Text);
        snapshotCmd.Parameters.AddWithValue("source", memory.Source);
        snapshotCmd.Parameters.AddWithValue("tags", memory.Tags ?? Array.Empty<string>());
        snapshotCmd.Parameters.AddWithValue("confidence", (double)memory.Confidence);
        snapshotCmd.Parameters.AddWithValue("title", (object?)memory.Title ?? DBNull.Value);
        snapshotCmd.Parameters.AddWithValue("relationshipIds", relationshipIds);
        snapshotCmd.Parameters.AddWithValue("createdAt", memory.CreatedAt);
        await snapshotCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ==================== PROVIDER SETTINGS ====================

    public async Task<ProviderSettings?> GetActiveProviderAsync(string providerType, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at
            FROM provider_settings
            WHERE provider_type = @providerType AND is_active = true
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("providerType", providerType);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadProviderSettings(reader);
        }

        return null;
    }

    public async Task<IReadOnlyList<ProviderSettings>> GetAllProvidersAsync(string providerType, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at
            FROM provider_settings
            WHERE provider_type = @providerType
            ORDER BY display_name, provider_name";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("providerType", providerType);

        var providers = new List<ProviderSettings>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            providers.Add(ReadProviderSettings(reader));
        }

        return providers;
    }

    public async Task<ProviderSettings> SaveProviderSettingsAsync(ProviderSettings settings, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Upsert based on (provider_type, provider_name) unique constraint
        const string sql = @"
            INSERT INTO provider_settings (id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at)
            VALUES (@id, @providerType, @providerName, @displayName, @config, @isActive, @createdAt, @updatedAt)
            ON CONFLICT (provider_type, provider_name) DO UPDATE SET
                display_name = EXCLUDED.display_name,
                config = EXCLUDED.config,
                is_active = EXCLUDED.is_active,
                updated_at = EXCLUDED.updated_at
            RETURNING id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at";

        await using var cmd = new NpgsqlCommand(sql, connection);
        var id = settings.Id.Value == Guid.Empty ? Guid.NewGuid() : settings.Id.Value;
        var now = DateTime.UtcNow;

        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("providerType", settings.ProviderType);
        cmd.Parameters.AddWithValue("providerName", settings.ProviderName);
        cmd.Parameters.AddWithValue("displayName", (object?)settings.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("config", settings.Config);
        cmd.Parameters.AddWithValue("isActive", settings.IsActive);
        cmd.Parameters.AddWithValue("createdAt", settings.CreatedAt == default ? now : settings.CreatedAt);
        cmd.Parameters.AddWithValue("updatedAt", now);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadProviderSettings(reader);
        }

        throw new InvalidOperationException("Failed to save provider settings");
    }

    public async Task SetActiveProviderAsync(string providerType, string providerName, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // First, deactivate all providers of this type
            const string deactivateSql = @"
                UPDATE provider_settings
                SET is_active = false, updated_at = NOW()
                WHERE provider_type = @providerType";

            await using (var deactivateCmd = new NpgsqlCommand(deactivateSql, connection, transaction))
            {
                deactivateCmd.Parameters.AddWithValue("providerType", providerType);
                await deactivateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Then, activate the specified provider
            const string activateSql = @"
                UPDATE provider_settings
                SET is_active = true, updated_at = NOW()
                WHERE provider_type = @providerType AND provider_name = @providerName";

            await using (var activateCmd = new NpgsqlCommand(activateSql, connection, transaction))
            {
                activateCmd.Parameters.AddWithValue("providerType", providerType);
                activateCmd.Parameters.AddWithValue("providerName", providerName);
                var rowsAffected = await activateCmd.ExecuteNonQueryAsync(cancellationToken);

                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException($"Provider '{providerName}' of type '{providerType}' not found");
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static ProviderSettings ReadProviderSettings(NpgsqlDataReader reader)
    {
        return new ProviderSettings
        {
            Id = (ProviderSettingsId)reader.GetGuid(0),
            ProviderType = reader.GetString(1),
            ProviderName = reader.GetString(2),
            DisplayName = reader.IsDBNull(3) ? null : reader.GetString(3),
            Config = reader.GetFieldValue<JsonDocument>(4),
            IsActive = reader.GetBoolean(5),
            CreatedAt = reader.GetDateTime(6),
            UpdatedAt = reader.GetDateTime(7)
        };
    }

    /// <summary>
    /// Reads a Memory from reader with columns: id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype
    /// Optionally includes similarity at index 16.
    /// </summary>
    private static Memorizer.Models.Memory ReadMemoryFromReader(NpgsqlDataReader reader, bool withSimilarity = false)
    {
        // Read owner information
        var ownerType = (OwnerTypeEnum)reader.GetInt16(13);
        var ownerId = reader.GetGuid(14);
        var owner = new MemoryOwner { Type = ownerType, Id = ownerId };

        return new Memorizer.Models.Memory
        {
            Id = (MemoryId)reader.GetGuid(0),
            Type = reader.GetString(1),
            Content = reader.GetFieldValue<JsonDocument>(2),
            Text = reader.GetString(3),
            Source = reader.GetString(4),
            Embedding = reader.IsDBNull(5) ? null : reader.GetFieldValue<Vector>(5),
            EmbeddingMetadata = reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector>(6),
            Tags = reader.GetFieldValue<string[]>(7),
            Confidence = new Confidence(reader.GetDouble(8)),
            CreatedAt = reader.GetDateTime(9),
            UpdatedAt = reader.GetDateTime(10),
            Title = reader.IsDBNull(11) ? null : reader.GetString(11),
            CurrentVersion = new VersionNumber(reader.GetInt32(12)),
            Owner = owner,
            Archetype = (ArchetypeEnum)reader.GetInt16(15),
            Similarity = withSimilarity && !reader.IsDBNull(16)
                ? SimilarityScore.FromDistance(reader.GetDouble(16))
                : null
        };
    }

    /// <summary>
    /// Reads a MemoryRelationship from reader with columns: id, from_memory_id, to_memory_id, type, created_at, score, created_in_version, deleted_in_version
    /// </summary>
    private static MemoryRelationship ReadRelationshipFromReader(NpgsqlDataReader reader)
    {
        return new MemoryRelationship
        {
            Id = (RelationshipId)reader.GetGuid(0),
            FromMemoryId = (MemoryId)reader.GetGuid(1),
            ToMemoryId = (MemoryId)reader.GetGuid(2),
            Type = reader.GetString(3),
            CreatedAt = reader.GetDateTime(4),
            Score = reader.IsDBNull(5) ? null : new SimilarityScore(reader.GetDouble(5)),
            CreatedInVersion = reader.IsDBNull(6) ? null : new VersionNumber(reader.GetInt32(6)),
            DeletedInVersion = reader.IsDBNull(7) ? null : new VersionNumber(reader.GetInt32(7))
        };
    }

    /// <summary>
    /// Reads a MemoryEvent from reader with columns: event_id, memory_id, version_number, event_type, event_data, timestamp, changed_by
    /// </summary>
    private static MemoryEvent ReadEventFromReader(NpgsqlDataReader reader)
    {
        return new MemoryEvent
        {
            EventId = (Models.EventId)reader.GetGuid(0),
            MemoryId = (MemoryId)reader.GetGuid(1),
            VersionNumber = new VersionNumber(reader.GetInt32(2)),
            EventType = reader.GetString(3),
            EventData = reader.GetFieldValue<JsonDocument>(4),
            Timestamp = reader.GetDateTime(5),
            ChangedBy = reader.IsDBNull(6) ? null : reader.GetString(6)
        };
    }

    /// <summary>
    /// Reads a MemoryVersion from reader with columns: version_id, memory_id, version_number, type, content, text, source, tags, confidence, title, relationship_ids, created_at, versioned_at
    /// </summary>
    private static MemoryVersion ReadVersionFromReader(NpgsqlDataReader reader)
    {
        return new MemoryVersion
        {
            VersionId = (VersionId)reader.GetGuid(0),
            MemoryId = (MemoryId)reader.GetGuid(1),
            VersionNumber = new VersionNumber(reader.GetInt32(2)),
            Type = reader.GetString(3),
            Content = reader.GetFieldValue<JsonDocument>(4),
            Text = reader.GetString(5),
            Source = reader.GetString(6),
            Tags = reader.GetFieldValue<string[]>(7),
            Confidence = new Confidence(reader.GetDouble(8)),
            Title = reader.IsDBNull(9) ? null : reader.GetString(9),
            RelationshipIds = reader.GetFieldValue<Guid[]>(10).Select(g => (RelationshipId)g).ToArray(),
            CreatedAt = reader.GetDateTime(11),
            VersionedAt = reader.GetDateTime(12)
        };
    }

    // ===== Workspace Operations =====

    public async Task<Workspace> CreateWorkspaceAsync(
        string name,
        string? description = null,
        WorkspaceId? parentId = null,
        CancellationToken cancellationToken = default)
    {
        var slug = GenerateSlug(name);
        var id = WorkspaceId.New();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO workspaces (id, parent_id, name, slug, description, is_system, created_at, updated_at)
            VALUES (@id, @parentId, @name, @slug, @description, false, NOW(), NOW())
            RETURNING id, parent_id, name, slug, description, is_system, settings, created_at, updated_at", conn);

        cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("parentId", parentId?.Value ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("slug", slug);
        cmd.Parameters.AddWithValue("description", description ?? (object)DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var workspace = ReadWorkspaceFromReader(reader);

        // Create system memory for semantic search on workspace metadata
        await CreateOrUpdateWorkspaceSystemMemoryAsync(workspace, cancellationToken);

        return workspace;
    }

    public async Task<Workspace?> GetWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, parent_id, name, slug, description, is_system, settings, created_at, updated_at
            FROM workspaces WHERE id = @id", conn);

        cmd.Parameters.AddWithValue("id", id.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadWorkspaceFromReader(reader);
    }

    public async Task<Workspace?> GetWorkspaceBySlugAsync(string slug, WorkspaceId? parentId = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, parent_id, name, slug, description, is_system, settings, created_at, updated_at
            FROM workspaces
            WHERE slug = @slug AND (parent_id = @parentId OR (@parentId IS NULL AND parent_id IS NULL))", conn);

        cmd.Parameters.AddWithValue("slug", slug);
        cmd.Parameters.AddWithValue("parentId", parentId?.Value ?? (object)DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadWorkspaceFromReader(reader);
    }

    public async Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(
        WorkspaceId? parentId = null,
        bool includeSystem = false,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT id, parent_id, name, slug, description, is_system, settings, created_at, updated_at
            FROM workspaces
            WHERE (parent_id = @parentId OR (@parentId IS NULL AND parent_id IS NULL))";

        if (!includeSystem)
            sql += " AND is_system = false";

        sql += " ORDER BY name";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("parentId", parentId?.Value ?? (object)DBNull.Value);

        var results = new List<Workspace>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadWorkspaceFromReader(reader));
        }
        return results;
    }

    public async Task<Workspace> UpdateWorkspaceAsync(
        WorkspaceId id,
        string? name = null,
        string? description = null,
        WorkspaceId? newParentId = null,
        bool makeTopLevel = false,
        CancellationToken cancellationToken = default)
    {
        if (newParentId != null && makeTopLevel)
            throw new InvalidOperationException("Cannot specify both newParentId and makeTopLevel. Use one or the other.");

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        if (newParentId != null)
        {
            // Self-reference check
            if (newParentId.Value == id)
                throw new InvalidOperationException("A workspace cannot be its own parent.");

            // Target must exist and not be a system workspace
            var targetParent = await GetWorkspaceAsync(newParentId.Value, cancellationToken);
            if (targetParent == null)
                throw new InvalidOperationException($"Target parent workspace {newParentId.Value.Value} not found.");
            if (targetParent.IsSystem)
                throw new InvalidOperationException("Cannot move a workspace under a system workspace.");

            // Circular reference check: newParentId must not be a descendant of id
            if (await IsWorkspaceDescendantOfAsync(newParentId.Value, id, conn, cancellationToken))
                throw new InvalidOperationException("Cannot move a workspace under its own descendant. This would create a circular reference.");
        }

        bool updateParent = newParentId != null || makeTopLevel;
        var slug = name != null ? GenerateSlug(name) : null;

        // Capture old slug before updating (needed for markdown export folder rename)
        string? oldSlug = null;
        if (name != null && _markdownExportService is { IsEnabled: true })
        {
            var existingWorkspace = await GetWorkspaceAsync(id, cancellationToken);
            oldSlug = existingWorkspace?.Slug;
        }

        var sql = @"
            UPDATE workspaces SET
                name = COALESCE(@name, name),
                slug = CASE WHEN @name IS NOT NULL THEN @slug ELSE slug END,
                description = COALESCE(@description, description)," +
            (updateParent ? @"
                parent_id = @newParentId," : "") + @"
                updated_at = NOW()
            WHERE id = @id AND is_system = false
            RETURNING id, parent_id, name, slug, description, is_system, settings, created_at, updated_at";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("name", name ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("slug", slug ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("description", description ?? (object)DBNull.Value);
        if (updateParent)
            cmd.Parameters.AddWithValue("newParentId", makeTopLevel ? DBNull.Value : newParentId!.Value.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Workspace {id} not found or is a system workspace");

        var workspace = ReadWorkspaceFromReader(reader);

        // Update system memory if searchable fields changed
        if (name != null || description != null)
        {
            await CreateOrUpdateWorkspaceSystemMemoryAsync(workspace, cancellationToken);
        }

        // Rename markdown export folder if name changed
        if (name != null && oldSlug != null && oldSlug != workspace.Slug && _markdownExportService is { IsEnabled: true })
        {
            try { await _markdownExportService.RenameWorkspaceFolderAsync(id, oldSlug, workspace.Slug, cancellationToken); }
            catch { /* Don't fail the update operation */ }
        }

        return workspace;
    }

    /// <summary>
    /// Checks if workspaceId is a descendant of potentialAncestorId.
    /// Used to prevent circular references when reparenting workspaces.
    /// </summary>
    private async Task<bool> IsWorkspaceDescendantOfAsync(WorkspaceId workspaceId, WorkspaceId potentialAncestorId, NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(@"
            WITH RECURSIVE descendants AS (
                SELECT id FROM workspaces WHERE parent_id = @ancestorId
                UNION ALL
                SELECT w.id FROM workspaces w
                INNER JOIN descendants d ON w.parent_id = d.id
            )
            SELECT EXISTS (SELECT 1 FROM descendants WHERE id = @workspaceId)", conn);

        cmd.Parameters.AddWithValue("ancestorId", potentialAncestorId.Value);
        cmd.Parameters.AddWithValue("workspaceId", workspaceId.Value);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task<Project> MoveProjectToWorkspaceAsync(
        ProjectId id,
        WorkspaceId newWorkspaceId,
        ProjectId? newParentId = null,
        CancellationToken cancellationToken = default)
    {
        // Load existing project
        var existing = await GetProjectAsync(id, cancellationToken);
        if (existing == null)
            throw new InvalidOperationException($"Project {id.Value} not found.");

        // Target workspace must exist and not be system
        var targetWorkspace = await GetWorkspaceAsync(newWorkspaceId, cancellationToken);
        if (targetWorkspace == null)
            throw new InvalidOperationException($"Target workspace {newWorkspaceId.Value} not found.");
        if (targetWorkspace.IsSystem)
            throw new InvalidOperationException("Cannot move a project into a system workspace.");

        // Already in target workspace
        if (existing.WorkspaceId == newWorkspaceId)
            throw new InvalidOperationException("Project is already in the specified workspace.");

        // Validate optional new parent
        if (newParentId != null)
        {
            var newParent = await GetProjectAsync(newParentId.Value, cancellationToken);
            if (newParent == null)
                throw new InvalidOperationException($"Target parent project {newParentId.Value.Value} not found.");
            if (newParent.WorkspaceId != newWorkspaceId)
                throw new InvalidOperationException("The new parent project must belong to the target workspace.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Step 1: Recursively move all descendants (including the root) to the new workspace
        await using (var updateCmd = new NpgsqlCommand(@"
            WITH RECURSIVE subtree AS (
                SELECT id FROM projects WHERE id = @rootId
                UNION ALL
                SELECT p.id FROM projects p
                INNER JOIN subtree s ON p.parent_id = s.id
            )
            UPDATE projects SET workspace_id = @newWorkspaceId
            WHERE id IN (SELECT id FROM subtree)", conn))
        {
            updateCmd.Parameters.AddWithValue("rootId", id.Value);
            updateCmd.Parameters.AddWithValue("newWorkspaceId", newWorkspaceId.Value);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Step 2: Update the root project's parent_id and return it
        await using var returnCmd = new NpgsqlCommand(@"
            UPDATE projects SET
                parent_id = @newParentId,
                updated_at = NOW()
            WHERE id = @id
            RETURNING id, workspace_id, parent_id, name, slug, description, status, victory_conditions, settings, created_at, updated_at, completed_at", conn);

        returnCmd.Parameters.AddWithValue("id", id.Value);
        returnCmd.Parameters.AddWithValue("newParentId", newParentId.HasValue ? newParentId.Value.Value : DBNull.Value);

        await using var reader = await returnCmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Project {id} not found after move.");

        var project = ReadProjectFromReader(reader);

        // Update system memory to reflect new workspace
        await CreateOrUpdateProjectSystemMemoryAsync(project, cancellationToken);

        return project;
    }

    public async Task DeleteWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default)
    {
        // Delete the system memory first (before the workspace)
        await DeleteWorkspaceSystemMemoryAsync(id, cancellationToken);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            DELETE FROM workspaces WHERE id = @id AND is_system = false", conn);

        cmd.Parameters.AddWithValue("id", id.Value);

        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
            throw new InvalidOperationException($"Workspace {id} not found or is a system workspace");
    }

    public async Task<IReadOnlyList<WorkspaceSearchResult>> SearchWorkspacesAsync(
        string query,
        bool includeSystem = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<WorkspaceSearchResult>();

        // First, try semantic search using system memories
        var semanticMatches = await SearchWorkspacesBySystemMemoryAsync(query, limit: 50, minSimilarity: 0.5, cancellationToken);

        List<Workspace> workspaces;

        if (semanticMatches.Count > 0)
        {
            // Fetch full workspace details for matched IDs
            var workspaceIds = semanticMatches.Select(m => m.Id).ToList();
            workspaces = await GetWorkspacesByIdsAsync(workspaceIds, includeSystem, cancellationToken);
        }
        else
        {
            // Fall back to ILIKE search for backward compatibility
            // (handles workspaces without system memories, e.g., created before this feature)
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            var sql = @"
                SELECT id, parent_id, name, slug, description, is_system, settings, created_at, updated_at
                FROM workspaces
                WHERE name ILIKE '%' || @query || '%'";

            if (!includeSystem)
                sql += " AND is_system = false";

            sql += " ORDER BY name LIMIT 50";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("query", query);

            workspaces = new List<Workspace>();
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    workspaces.Add(ReadWorkspaceFromReader(reader));
                }
            }
        }

        // Build results with paths
        var results = new List<WorkspaceSearchResult>();
        foreach (var workspace in workspaces)
        {
            var path = await GetWorkspacePathAsync(workspace.Id, cancellationToken);
            results.Add(new WorkspaceSearchResult
            {
                Workspace = workspace,
                Path = path
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<WorkspacePathSegment>> GetWorkspacePathAsync(
        WorkspaceId id,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Use recursive CTE to get ancestor chain
        await using var cmd = new NpgsqlCommand(@"
            WITH RECURSIVE ancestors AS (
                -- Start with the workspace's parent
                SELECT w.id, w.parent_id, w.name, 0 as depth
                FROM workspaces w
                INNER JOIN workspaces child ON child.parent_id = w.id
                WHERE child.id = @id

                UNION ALL

                -- Walk up the tree
                SELECT w.id, w.parent_id, w.name, a.depth + 1
                FROM workspaces w
                INNER JOIN ancestors a ON w.id = a.parent_id
            )
            SELECT id, name FROM ancestors ORDER BY depth DESC", conn);

        cmd.Parameters.AddWithValue("id", id.Value);

        var path = new List<WorkspacePathSegment>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            path.Add(new WorkspacePathSegment(
                new WorkspaceId(reader.GetGuid(0)),
                reader.GetString(1)
            ));
        }

        return path;
    }

    // ===== Project Operations =====

    private async Task<bool> WouldCreateCycleAsync(
        ProjectId childId,
        ProjectId? parentId,
        CancellationToken cancellationToken)
    {
        if (!parentId.HasValue)
            return false;

        var current = parentId;
        var visited = new HashSet<Guid> { childId.Value };

        while (current.HasValue)
        {
            if (visited.Contains(current.Value.Value))
                return true;

            visited.Add(current.Value.Value);
            var project = await GetProjectAsync(current.Value, cancellationToken);
            if (project == null)
                break;

            current = project.ParentId;
        }

        return false;
    }

    public async Task<Project> CreateProjectAsync(
        WorkspaceId workspaceId,
        string name,
        string? description = null,
        ProjectId? parentId = null,
        CancellationToken cancellationToken = default)
    {
        // Validate parent if specified
        if (parentId.HasValue)
        {
            var parent = await GetProjectAsync(parentId.Value, cancellationToken);
            if (parent == null)
                throw new InvalidOperationException($"Parent project {parentId.Value} not found");

            // Verify parent is in same workspace
            if (parent.WorkspaceId != workspaceId)
                throw new InvalidOperationException("Parent project must be in same workspace");

            // Note: Circular reference check not needed for new projects since they don't exist yet
        }

        var slug = GenerateSlug(name);
        var id = ProjectId.New();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO projects (id, workspace_id, parent_id, name, slug, description, status, created_at, updated_at)
            VALUES (@id, @workspaceId, @parentId, @name, @slug, @description, 0, NOW(), NOW())
            RETURNING id, workspace_id, parent_id, name, slug, description, status, victory_conditions, settings, created_at, updated_at, completed_at", conn);

        cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("workspaceId", workspaceId.Value);
        cmd.Parameters.AddWithValue("parentId", parentId?.Value ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("slug", slug);
        cmd.Parameters.AddWithValue("description", description ?? (object)DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var project = ReadProjectFromReader(reader);

        // Create system memory for semantic search
        await CreateOrUpdateProjectSystemMemoryAsync(project, cancellationToken);

        return project;
    }

    public async Task<Project?> GetProjectAsync(ProjectId id, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, workspace_id, parent_id, name, slug, description, status, victory_conditions, settings, created_at, updated_at, completed_at
            FROM projects WHERE id = @id", conn);

        cmd.Parameters.AddWithValue("id", id.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadProjectFromReader(reader);
    }

    public async Task<IReadOnlyList<Project>> GetProjectsAsync(
        WorkspaceId workspaceId,
        ProjectId? parentId = null,
        ProjectStatusEnum? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT id, workspace_id, parent_id, name, slug, description, status, victory_conditions, settings, created_at, updated_at, completed_at
            FROM projects
            WHERE workspace_id = @workspaceId
              AND (parent_id = @parentId OR (@parentId IS NULL AND parent_id IS NULL))";

        if (statusFilter.HasValue)
            sql += " AND status = @status";

        sql += " ORDER BY name";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("workspaceId", workspaceId.Value);
        cmd.Parameters.AddWithValue("parentId", parentId?.Value ?? (object)DBNull.Value);
        if (statusFilter.HasValue)
            cmd.Parameters.AddWithValue("status", (short)statusFilter.Value);

        var results = new List<Project>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadProjectFromReader(reader));
        }
        return results;
    }

    public async Task<Project> UpdateProjectAsync(
        ProjectId id,
        string? name = null,
        string? description = null,
        ProjectStatusEnum? status = null,
        string? victoryConditions = null,
        ProjectId? newParentId = null,
        bool makeTopLevel = false,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Validate that both newParentId and makeTopLevel are not specified together
        if (newParentId != null && makeTopLevel)
        {
            throw new InvalidOperationException("Cannot specify both newParentId and makeTopLevel. Use one or the other.");
        }

        // Validate circular reference if moving to a new parent
        if (newParentId != null)
        {
            // Check for self-reference: a project cannot be its own parent
            if (newParentId.Value == id)
            {
                throw new InvalidOperationException("A project cannot be its own parent.");
            }

            // Verify the new parent exists and is in the same workspace
            var newParent = await GetProjectAsync(newParentId.Value, cancellationToken);
            if (newParent == null)
            {
                throw new InvalidOperationException($"Target parent project {newParentId.Value.Value} not found.");
            }

            // Check for circular reference: ensure newParentId is not a descendant of the current project
            if (await IsDescendantOfAsync(newParentId.Value, id, conn, cancellationToken))
            {
                throw new InvalidOperationException("Cannot move a project under its own descendant. This would create a circular reference.");
            }

            // Verify same workspace
            var currentProject = await GetProjectAsync(id, cancellationToken);
            if (currentProject != null && currentProject.WorkspaceId != newParent.WorkspaceId)
            {
                throw new InvalidOperationException("Cannot move a project to a parent in a different workspace.");
            }
        }

        var slug = name != null ? GenerateSlug(name) : null;

        // Capture old slug before updating (needed for markdown export folder rename)
        string? oldProjectSlug = null;
        if (name != null && _markdownExportService is { IsEnabled: true })
        {
            var existingProject = await GetProjectAsync(id, cancellationToken);
            oldProjectSlug = existingProject?.Slug;
        }

        // Determine how to handle parent_id in the UPDATE
        // If newParentId is specified, set parent_id to that value
        // If makeTopLevel is true, set parent_id to NULL
        // Otherwise, keep the existing value (COALESCE pattern won't work for setting NULL explicitly)
        bool updateParent = newParentId != null || makeTopLevel;

        var sql = @"
            UPDATE projects SET
                name = COALESCE(@name, name),
                slug = CASE WHEN @name IS NOT NULL THEN @slug ELSE slug END,
                description = COALESCE(@description, description),
                status = COALESCE(@status, status),
                victory_conditions = COALESCE(@victoryConditions, victory_conditions)," +
            (updateParent ? @"
                parent_id = @newParentId," : "") + @"
                updated_at = NOW(),
                completed_at = CASE
                    WHEN @status IN (3, 4) AND completed_at IS NULL THEN NOW()
                    WHEN @status NOT IN (3, 4) THEN NULL
                    ELSE completed_at
                END
            WHERE id = @id
            RETURNING id, workspace_id, parent_id, name, slug, description, status, victory_conditions, settings, created_at, updated_at, completed_at";

        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("name", name ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("slug", slug ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("description", description ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.HasValue ? (short)status.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("victoryConditions", victoryConditions ?? (object)DBNull.Value);

        if (updateParent)
        {
            // makeTopLevel sets to NULL, otherwise use the newParentId
            cmd.Parameters.AddWithValue("newParentId", makeTopLevel ? DBNull.Value : newParentId!.Value.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Project {id} not found");

        var project = ReadProjectFromReader(reader);

        // Update system memory if searchable fields changed (name, description, victoryConditions, status)
        if (name != null || description != null || victoryConditions != null || status != null)
        {
            await CreateOrUpdateProjectSystemMemoryAsync(project, cancellationToken);
        }

        // Rename markdown export folder if name changed
        if (name != null && oldProjectSlug != null && oldProjectSlug != project.Slug && _markdownExportService is { IsEnabled: true })
        {
            try { await _markdownExportService.RenameProjectFolderAsync(id, oldProjectSlug, project.Slug, cancellationToken); }
            catch { /* Don't fail the update operation */ }
        }

        return project;
    }

    /// <summary>
    /// Checks if projectId is a descendant of potentialAncestorId.
    /// Used to prevent circular references when moving projects.
    /// </summary>
    private async Task<bool> IsDescendantOfAsync(ProjectId projectId, ProjectId potentialAncestorId, NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // Use recursive CTE to find all descendants of potentialAncestorId
        await using var cmd = new NpgsqlCommand(@"
            WITH RECURSIVE descendants AS (
                -- Base case: direct children of the potential ancestor
                SELECT id FROM projects WHERE parent_id = @ancestorId
                UNION ALL
                -- Recursive case: children of descendants
                SELECT p.id FROM projects p
                INNER JOIN descendants d ON p.parent_id = d.id
            )
            SELECT EXISTS (SELECT 1 FROM descendants WHERE id = @projectId)", conn);

        cmd.Parameters.AddWithValue("ancestorId", potentialAncestorId.Value);
        cmd.Parameters.AddWithValue("projectId", projectId.Value);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task DeleteProjectAsync(ProjectId id, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("DELETE FROM projects WHERE id = @id", conn);

        cmd.Parameters.AddWithValue("id", id.Value);

        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
            throw new InvalidOperationException($"Project {id} not found");

        // Delete the system memory for this project
        await DeleteProjectSystemMemoryAsync(id, cancellationToken);
    }

    #region Project System Memory Management

    /// <summary>
    /// System memory type identifier for project index entries.
    /// </summary>
    private const string ProjectSystemMemoryType = "system:project-index";

    /// <summary>
    /// Generates searchable text content from project metadata.
    /// This text is embedded for semantic search.
    /// </summary>
    private static string GenerateProjectSystemMemoryText(Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Project: {project.Name}");

        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            sb.AppendLine($"Description: {project.Description}");
        }

        if (!string.IsNullOrWhiteSpace(project.VictoryConditions))
        {
            sb.AppendLine($"Victory Conditions: {project.VictoryConditions}");
        }

        sb.AppendLine($"Status: {project.Status.ToStringValue()}");

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Creates or updates the system memory for a project.
    /// This enables semantic search on project metadata.
    /// </summary>
    private async Task CreateOrUpdateProjectSystemMemoryAsync(
        Project project,
        CancellationToken cancellationToken = default)
    {
        var text = GenerateProjectSystemMemoryText(project);
        var title = $"[Project Index] {project.Name}";
        var tags = new[] { "system", "project-index", $"project:{project.Id.Value}" };
        var entityTag = $"project:{project.Id.Value}";

        // Check if a system memory already exists for this project (lookup by tag)
        var existingMemoryId = await GetSystemMemoryIdByTagAsync(entityTag, ProjectSystemMemoryType, cancellationToken);

        if (existingMemoryId.HasValue)
        {
            // Update existing system memory (creates new version with 100% content overwrite)
            await UpdateMemory(
                existingMemoryId.Value,
                type: ProjectSystemMemoryType,
                content: text,
                source: "system",
                tags: tags,
                confidence: new Confidence(1.0),
                title: title,
                cancellationToken: cancellationToken
            );
        }
        else
        {
            // Create new system memory in the System Memories workspace
            await StoreMemory(
                type: ProjectSystemMemoryType,
                content: text,
                source: "system",
                tags: tags,
                confidence: new Confidence(1.0),
                title: title,
                owner: MemoryOwner.SystemMemories,
                archetype: ArchetypeEnum.System,
                cancellationToken: cancellationToken
            );
        }
    }

    /// <summary>
    /// Gets the system memory ID by entity tag (e.g., "project:{id}" or "workspace:{id}").
    /// System memories are stored in the System Memories workspace but identified by their entity tag.
    /// </summary>
    private async Task<MemoryId?> GetSystemMemoryIdByTagAsync(
        string entityTag,
        string systemMemoryType,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id FROM memories
            WHERE archetype = 3
            AND type_legacy = @type
            AND @tag = ANY(tags)
            LIMIT 1", conn);

        cmd.Parameters.AddWithValue("type", systemMemoryType);
        cmd.Parameters.AddWithValue("tag", entityTag);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is Guid id ? new MemoryId(id) : null;
    }

    /// <summary>
    /// Deletes the system memory for a project.
    /// </summary>
    private Task DeleteProjectSystemMemoryAsync(ProjectId projectId, CancellationToken cancellationToken = default)
        => DeleteSystemMemoryByTagAsync($"project:{projectId.Value}", ProjectSystemMemoryType, cancellationToken);

    /// <summary>
    /// Deletes the system memory by entity tag (e.g., "project:{id}" or "workspace:{id}").
    /// </summary>
    private async Task DeleteSystemMemoryByTagAsync(
        string entityTag,
        string systemMemoryType,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            DELETE FROM memories
            WHERE archetype = 3
            AND type_legacy = @type
            AND @tag = ANY(tags)", conn);

        cmd.Parameters.AddWithValue("type", systemMemoryType);
        cmd.Parameters.AddWithValue("tag", entityTag);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Searches for projects using hybrid search (vector + FTS) on system memories.
    /// Returns project IDs that match the query.
    /// </summary>
    private async Task<List<(ProjectId Id, double Similarity)>> SearchProjectsBySystemMemoryAsync(
        string query,
        int limit = 50,
        double minSimilarity = 0.5,
        CancellationToken cancellationToken = default)
    {
        var taggedResults = await HybridSearchSystemMemories(query, ProjectSystemMemoryType, limit, cancellationToken);

        var results = new List<(ProjectId, double)>();
        foreach (var (tags, similarity, hadFtsMatch) in taggedResults)
        {
            // Keep FTS matches (textually relevant); filter vector-only matches by similarity
            if (!hadFtsMatch && similarity < minSimilarity) continue;

            var projectTag = tags.FirstOrDefault(t => t.StartsWith("project:"));
            if (projectTag != null && Guid.TryParse(projectTag.Substring("project:".Length), out var projectGuid))
            {
                results.Add((new ProjectId(projectGuid), similarity));
            }
        }

        return results;
    }

    #endregion

    #region Workspace System Memory Management

    /// <summary>
    /// System memory type identifier for workspace index entries.
    /// </summary>
    private const string WorkspaceSystemMemoryType = "system:workspace-index";

    /// <summary>
    /// Generates searchable text content from workspace metadata.
    /// This text is embedded for semantic search.
    /// </summary>
    private static string GenerateWorkspaceSystemMemoryText(Workspace workspace)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Workspace: {workspace.Name}");

        if (!string.IsNullOrWhiteSpace(workspace.Description))
        {
            sb.AppendLine($"Description: {workspace.Description}");
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Creates or updates the system memory for a workspace.
    /// This enables semantic search on workspace metadata.
    /// </summary>
    private async Task CreateOrUpdateWorkspaceSystemMemoryAsync(
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        var text = GenerateWorkspaceSystemMemoryText(workspace);
        var title = $"[Workspace Index] {workspace.Name}";
        var tags = new[] { "system", "workspace-index", $"workspace:{workspace.Id.Value}" };
        var entityTag = $"workspace:{workspace.Id.Value}";

        // Check if a system memory already exists for this workspace (lookup by tag)
        var existingMemoryId = await GetSystemMemoryIdByTagAsync(entityTag, WorkspaceSystemMemoryType, cancellationToken);

        if (existingMemoryId.HasValue)
        {
            // Update existing system memory (creates new version with 100% content overwrite)
            await UpdateMemory(
                existingMemoryId.Value,
                type: WorkspaceSystemMemoryType,
                content: text,
                source: "system",
                tags: tags,
                confidence: new Confidence(1.0),
                title: title,
                cancellationToken: cancellationToken
            );
        }
        else
        {
            // Create new system memory in the System Memories workspace
            await StoreMemory(
                type: WorkspaceSystemMemoryType,
                content: text,
                source: "system",
                tags: tags,
                confidence: new Confidence(1.0),
                title: title,
                owner: MemoryOwner.SystemMemories,
                archetype: ArchetypeEnum.System,
                cancellationToken: cancellationToken
            );
        }
    }

    /// <summary>
    /// Deletes the system memory for a workspace.
    /// </summary>
    private Task DeleteWorkspaceSystemMemoryAsync(WorkspaceId workspaceId, CancellationToken cancellationToken = default)
        => DeleteSystemMemoryByTagAsync($"workspace:{workspaceId.Value}", WorkspaceSystemMemoryType, cancellationToken);

    /// <summary>
    /// Shared hybrid search for system memories (projects, workspaces).
    /// Combines vector search + FTS with RRF fusion, returning tags and similarity for each match.
    /// </summary>
    private async Task<List<(string[] Tags, double Similarity, bool HadFtsMatch)>> HybridSearchSystemMemories(
        string query,
        string systemMemoryType,
        int limit,
        CancellationToken cancellationToken)
    {
        float[] queryEmbedding = await _embeddingService.Generate(query, cancellationToken);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        int fetchLimit = Math.Max(limit * 2, 20);

        // Leg 1: Vector search (no hard threshold)
        var vectorResults = new List<(string[] Tags, double Distance, int Rank)>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT tags, embedding_metadata <=> @embedding AS distance
            FROM memories
            WHERE archetype = 3
            AND type_legacy = @type
            AND embedding_metadata IS NOT NULL
            ORDER BY embedding_metadata <=> @embedding
            LIMIT @fetchLimit", conn))
        {
            cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
            cmd.Parameters.AddWithValue("type", systemMemoryType);
            cmd.Parameters.AddWithValue("fetchLimit", fetchLimit);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            int rank = 1;
            while (await reader.ReadAsync(cancellationToken))
            {
                var tags = reader.GetFieldValue<string[]>(0);
                var distance = reader.GetDouble(1);
                vectorResults.Add((tags, distance, rank++));
            }
        }

        // Leg 2: Full-text search (AND with prefix matching)
        string tsquery = BuildPrefixTsQuery(query);
        var ftsResults = new List<(string[] Tags, double FtsRank, int Rank)>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT tags, ts_rank_cd(search_vector, to_tsquery('english', @tsquery)) AS fts_rank
            FROM memories
            WHERE archetype = 3
            AND type_legacy = @type
            AND search_vector @@ to_tsquery('english', @tsquery)
            ORDER BY fts_rank DESC
            LIMIT @fetchLimit", conn))
        {
            cmd.Parameters.AddWithValue("tsquery", tsquery);
            cmd.Parameters.AddWithValue("type", systemMemoryType);
            cmd.Parameters.AddWithValue("fetchLimit", fetchLimit);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            int rank = 1;
            while (await reader.ReadAsync(cancellationToken))
            {
                var tags = reader.GetFieldValue<string[]>(0);
                var ftsRank = reader.GetDouble(1);
                ftsResults.Add((tags, ftsRank, rank++));
            }
        }

        // RRF fusion (k=60), adaptive weighting
        const int k = 60;
        int wordCount = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        double ftsWeight = wordCount <= 2 ? 1.5 : 1.0;
        double vectorWeight = 1.0;

        // Use a stable key to identify entries — the entity tag (project:{guid} or workspace:{guid})
        string? EntityKey(string[] tags) => tags.FirstOrDefault(t => t.StartsWith("project:") || t.StartsWith("workspace:"));

        var rrfScores = new Dictionary<string, (double Score, string[] Tags, double Distance, bool HadFtsMatch)>();

        foreach (var (tags, distance, rank) in vectorResults)
        {
            var key = EntityKey(tags);
            if (key == null) continue;
            double score = vectorWeight / (k + rank);
            rrfScores[key] = (score, tags, distance, false);
        }

        foreach (var (tags, _, rank) in ftsResults)
        {
            var key = EntityKey(tags);
            if (key == null) continue;
            double score = ftsWeight / (k + rank);

            if (rrfScores.TryGetValue(key, out var existing))
            {
                rrfScores[key] = (existing.Score + score, existing.Tags, existing.Distance, true);
            }
            else
            {
                rrfScores[key] = (score, tags, 1.0, true); // distance=1.0 (no vector match)
            }
        }

        return rrfScores.Values
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => (x.Tags, 1.0 - x.Distance, x.HadFtsMatch)) // convert distance to similarity
            .ToList();
    }

    /// <summary>
    /// Searches for workspaces using hybrid search (vector + FTS) on system memories.
    /// Returns workspace IDs that match the query.
    /// </summary>
    private async Task<List<(WorkspaceId Id, double Similarity)>> SearchWorkspacesBySystemMemoryAsync(
        string query,
        int limit = 50,
        double minSimilarity = 0.5,
        CancellationToken cancellationToken = default)
    {
        var taggedResults = await HybridSearchSystemMemories(query, WorkspaceSystemMemoryType, limit, cancellationToken);

        var results = new List<(WorkspaceId, double)>();
        foreach (var (tags, similarity, hadFtsMatch) in taggedResults)
        {
            // Keep FTS matches (textually relevant); filter vector-only matches by similarity
            if (!hadFtsMatch && similarity < minSimilarity) continue;

            var workspaceTag = tags.FirstOrDefault(t => t.StartsWith("workspace:"));
            if (workspaceTag != null && Guid.TryParse(workspaceTag.Substring("workspace:".Length), out var workspaceGuid))
            {
                results.Add((new WorkspaceId(workspaceGuid), similarity));
            }
        }

        return results;
    }

    /// <summary>
    /// Gets workspaces by their IDs.
    /// </summary>
    private async Task<List<Workspace>> GetWorkspacesByIdsAsync(
        IEnumerable<WorkspaceId> workspaceIds,
        bool includeSystem = false,
        CancellationToken cancellationToken = default)
    {
        var ids = workspaceIds.ToList();
        if (ids.Count == 0)
            return new List<Workspace>();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT id, parent_id, name, slug, description, is_system, settings, created_at, updated_at
            FROM workspaces
            WHERE id = ANY(@ids)";

        if (!includeSystem)
            sql += " AND is_system = false";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ids", ids.Select(id => id.Value).ToArray());

        var results = new List<Workspace>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadWorkspaceFromReader(reader));
        }

        return results;
    }

    #endregion

    public async Task<(int ProjectsSeeded, int WorkspacesSeeded)> SeedProjectAndWorkspaceSystemMemoriesAsync(
        CancellationToken cancellationToken = default)
    {
        int projectsSeeded = 0;
        int workspacesSeeded = 0;

        // Get all projects without system memories
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Find all project IDs that don't have a system memory
        await using var projectCmd = new NpgsqlCommand(@"
            SELECT p.id, p.workspace_id, p.parent_id, p.name, p.slug, p.description, p.status,
                   p.victory_conditions, p.settings, p.created_at, p.updated_at, p.completed_at
            FROM projects p
            WHERE NOT EXISTS (
                SELECT 1 FROM memories m
                WHERE m.owner_type = 1  -- Project
                AND m.owner_id = p.id
                AND m.archetype = 3     -- System
                AND m.type_legacy = @projectType
            )", conn);

        projectCmd.Parameters.AddWithValue("projectType", ProjectSystemMemoryType);

        var projectsToSeed = new List<Project>();
        await using (var reader = await projectCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                projectsToSeed.Add(ReadProjectFromReader(reader));
            }
        }

        // Create system memories for each project
        foreach (var project in projectsToSeed)
        {
            await CreateOrUpdateProjectSystemMemoryAsync(project, cancellationToken);
            projectsSeeded++;
        }

        // Find all workspace IDs that don't have a system memory (excluding system workspaces like Unfiled)
        await using var workspaceCmd = new NpgsqlCommand(@"
            SELECT w.id, w.parent_id, w.name, w.slug, w.description, w.is_system, w.settings, w.created_at, w.updated_at
            FROM workspaces w
            WHERE w.is_system = false
            AND NOT EXISTS (
                SELECT 1 FROM memories m
                WHERE m.owner_type = 0  -- Workspace
                AND m.owner_id = w.id
                AND m.archetype = 3     -- System
                AND m.type_legacy = @workspaceType
            )", conn);

        workspaceCmd.Parameters.AddWithValue("workspaceType", WorkspaceSystemMemoryType);

        var workspacesToSeed = new List<Workspace>();
        await using (var reader = await workspaceCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                workspacesToSeed.Add(ReadWorkspaceFromReader(reader));
            }
        }

        // Create system memories for each workspace
        foreach (var workspace in workspacesToSeed)
        {
            await CreateOrUpdateWorkspaceSystemMemoryAsync(workspace, cancellationToken);
            workspacesSeeded++;
        }

        return (projectsSeeded, workspacesSeeded);
    }

    public async Task<IReadOnlyList<ProjectSearchResult>> SearchProjectsAsync(
        string query,
        ProjectStatusEnum? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<ProjectSearchResult>();

        // First, try semantic search using system memories
        var semanticMatches = await SearchProjectsBySystemMemoryAsync(query, limit: 50, minSimilarity: 0.5, cancellationToken);

        List<Project> projects;

        if (semanticMatches.Count > 0)
        {
            // Fetch full project details for matched IDs
            var projectIds = semanticMatches.Select(m => m.Id).ToList();
            projects = await GetProjectsByIdsAsync(projectIds, statusFilter, cancellationToken);
        }
        else
        {
            // Fall back to ILIKE search for backward compatibility
            // (handles projects without system memories, e.g., created before this feature)
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            var sql = @"
                SELECT id, workspace_id, parent_id, name, slug, description, status, victory_conditions, settings, created_at, updated_at, completed_at
                FROM projects
                WHERE name ILIKE '%' || @query || '%'";

            if (statusFilter.HasValue)
                sql += " AND status = @status";

            sql += " ORDER BY name LIMIT 50";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("query", query);
            if (statusFilter.HasValue)
                cmd.Parameters.AddWithValue("status", (short)statusFilter.Value);

            projects = new List<Project>();
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    projects.Add(ReadProjectFromReader(reader));
                }
            }
        }

        // Build results with paths
        var results = new List<ProjectSearchResult>();
        foreach (var project in projects)
        {
            var path = await GetProjectPathAsync(project.Id, cancellationToken);
            results.Add(new ProjectSearchResult
            {
                Project = project,
                WorkspacePath = path.WorkspacePath,
                ProjectPath = path.ProjectAncestors
            });
        }

        return results;
    }

    /// <summary>
    /// Fetches projects by their IDs, optionally filtering by status.
    /// Preserves the order of the input IDs.
    /// </summary>
    private async Task<List<Project>> GetProjectsByIdsAsync(
        List<ProjectId> projectIds,
        ProjectStatusEnum? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (projectIds.Count == 0)
            return new List<Project>();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT id, workspace_id, parent_id, name, slug, description, status, victory_conditions, settings, created_at, updated_at, completed_at
            FROM projects
            WHERE id = ANY(@ids)";

        if (statusFilter.HasValue)
            sql += " AND status = @status";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ids", projectIds.Select(p => p.Value).ToArray());
        if (statusFilter.HasValue)
            cmd.Parameters.AddWithValue("status", (short)statusFilter.Value);

        var projectDict = new Dictionary<Guid, Project>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var project = ReadProjectFromReader(reader);
                projectDict[project.Id.Value] = project;
            }
        }

        // Preserve original order (by similarity)
        return projectIds
            .Where(id => projectDict.ContainsKey(id.Value))
            .Select(id => projectDict[id.Value])
            .ToList();
    }

    public async Task<ProjectPath> GetProjectPathAsync(
        ProjectId id,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // First get the project to find its workspace
        var project = await GetProjectAsync(id, cancellationToken);
        if (project == null)
        {
            return new ProjectPath
            {
                WorkspacePath = Array.Empty<WorkspacePathSegment>(),
                ProjectAncestors = Array.Empty<ProjectPathSegment>()
            };
        }

        // Get workspace path (including the direct workspace)
        var workspacePath = new List<WorkspacePathSegment>();

        // Get the containing workspace
        var workspace = await GetWorkspaceAsync(project.WorkspaceId, cancellationToken);
        if (workspace != null)
        {
            // Get ancestors of the workspace
            var workspaceAncestors = await GetWorkspacePathAsync(project.WorkspaceId, cancellationToken);
            workspacePath.AddRange(workspaceAncestors);
            // Add the containing workspace itself
            workspacePath.Add(new WorkspacePathSegment(workspace.Id, workspace.Name));
        }

        // Get project ancestors using recursive CTE
        await using var cmd = new NpgsqlCommand(@"
            WITH RECURSIVE ancestors AS (
                -- Start with the project's parent
                SELECT p.id, p.parent_id, p.name, 0 as depth
                FROM projects p
                INNER JOIN projects child ON child.parent_id = p.id
                WHERE child.id = @id

                UNION ALL

                -- Walk up the tree
                SELECT p.id, p.parent_id, p.name, a.depth + 1
                FROM projects p
                INNER JOIN ancestors a ON p.id = a.parent_id
            )
            SELECT id, name FROM ancestors ORDER BY depth DESC", conn);

        cmd.Parameters.AddWithValue("id", id.Value);

        var projectPath = new List<ProjectPathSegment>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projectPath.Add(new ProjectPathSegment(
                reader.GetGuid(0),
                reader.GetString(1),
                IsWorkspace: false
            ));
        }

        return new ProjectPath
        {
            WorkspacePath = workspacePath,
            ProjectAncestors = projectPath
        };
    }

    // ===== Memory Owner Operations =====

    public async Task SetMemoryOwnerAsync(MemoryId memoryId, MemoryOwner owner, CancellationToken cancellationToken = default)
    {
        // Capture old owner before updating (needed for markdown export file move)
        MemoryOwner? oldOwner = null;
        if (_markdownExportService is { IsEnabled: true })
        {
            var existing = await Get(memoryId, cancellationToken);
            oldOwner = existing?.Owner;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE memories SET
                owner_type = @ownerType,
                owner_id = @ownerId,
                updated_at = NOW()
            WHERE id = @id", conn);

        cmd.Parameters.AddWithValue("id", memoryId.Value);
        cmd.Parameters.AddWithValue("ownerType", (short)owner.Type);
        cmd.Parameters.AddWithValue("ownerId", owner.Id);

        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
            throw new InvalidOperationException($"Memory {memoryId} not found");

        if (oldOwner.HasValue && _markdownExportService is { IsEnabled: true })
        {
            try { await _markdownExportService.MoveMemoryFileAsync(memoryId, oldOwner.Value, owner, cancellationToken); }
            catch { /* Don't fail the owner change operation */ }
        }
    }

    public Task MoveMemoryToUnfiledAsync(MemoryId memoryId, CancellationToken cancellationToken = default)
    {
        return SetMemoryOwnerAsync(memoryId, MemoryOwner.Unfiled, cancellationToken);
    }

    public async Task<IReadOnlyList<Memorizer.Models.Memory>> GetMemoriesByOwnerAsync(
        MemoryOwner owner,
        int page = 1,
        int pageSize = 50,
        string? memoryType = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var typeClause = !string.IsNullOrWhiteSpace(memoryType) ? " AND type_legacy = @memoryType" : "";
        // Exclude System memories (archetype = 3) and Archived memories (archetype = 2) from normal queries
        await using var cmd = new NpgsqlCommand($@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence,
                   created_at, updated_at, title, current_version, owner_type, owner_id, archetype
            FROM memories
            WHERE owner_type = @ownerType AND owner_id = @ownerId
              AND archetype IN (0, 1){typeClause}
            ORDER BY updated_at DESC
            LIMIT @limit OFFSET @offset", conn);

        cmd.Parameters.AddWithValue("ownerType", (short)owner.Type);
        cmd.Parameters.AddWithValue("ownerId", owner.Id);
        if (!string.IsNullOrWhiteSpace(memoryType)) cmd.Parameters.AddWithValue("memoryType", memoryType);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        var results = new List<Memorizer.Models.Memory>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadMemoryFromReader(reader, withSimilarity: false));
        }
        return results;
    }

    public async Task<int> GetMemoryCountByOwnerAsync(MemoryOwner owner, string? memoryType = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var typeClause = !string.IsNullOrWhiteSpace(memoryType) ? " AND type_legacy = @memoryType" : "";
        // Exclude System memories (archetype = 3) and Archived memories (archetype = 2) from normal queries
        await using var cmd = new NpgsqlCommand($@"
            SELECT COUNT(*) FROM memories
            WHERE owner_type = @ownerType AND owner_id = @ownerId
              AND archetype IN (0, 1){typeClause}", conn);

        cmd.Parameters.AddWithValue("ownerType", (short)owner.Type);
        cmd.Parameters.AddWithValue("ownerId", owner.Id);
        if (!string.IsNullOrWhiteSpace(memoryType)) cmd.Parameters.AddWithValue("memoryType", memoryType);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public Task<IReadOnlyList<Memorizer.Models.Memory>> GetUnfiledMemoriesAsync(
        int page = 1,
        int pageSize = 50,
        string? memoryType = null,
        CancellationToken cancellationToken = default)
    {
        return GetMemoriesByOwnerAsync(MemoryOwner.Unfiled, page, pageSize, memoryType, cancellationToken);
    }

    public Task<int> GetUnfiledMemoryCountAsync(string? memoryType = null, CancellationToken cancellationToken = default)
    {
        return GetMemoryCountByOwnerAsync(MemoryOwner.Unfiled, memoryType, cancellationToken);
    }

    public async Task<(IReadOnlyList<Memorizer.Models.Memory> Memories, int TotalCount)> GetMemoriesByTagAsync(
        string[] tags,
        int page = 1,
        int pageSize = 20,
        MemoryOwner? owner = null,
        string? memoryType = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Build tag filter: each tag must match at least one element (AND logic)
        var tagClauses = new List<string>();
        for (var i = 0; i < tags.Length; i++)
        {
            tagClauses.Add($"EXISTS (SELECT 1 FROM unnest(tags) t WHERE lower(t) = lower(@tag{i}))");
        }
        var tagFilter = string.Join(" AND ", tagClauses);

        // Build optional owner filter clause
        var ownerClause = "";
        if (owner.HasValue)
        {
            ownerClause = " AND owner_type = @ownerType AND owner_id = @ownerId";
        }

        // Build optional type filter clause
        var typeClause = "";
        if (!string.IsNullOrWhiteSpace(memoryType))
        {
            typeClause = " AND type_legacy = @memoryType";
        }

        void AddTagParams(NpgsqlCommand c)
        {
            for (var i = 0; i < tags.Length; i++)
                c.Parameters.AddWithValue($"tag{i}", tags[i]);
        }

        void AddOwnerParams(NpgsqlCommand c)
        {
            if (!owner.HasValue) return;
            c.Parameters.AddWithValue("ownerType", (short)owner.Value.Type);
            c.Parameters.AddWithValue("ownerId", owner.Value.Id);
        }

        void AddTypeParam(NpgsqlCommand c)
        {
            if (!string.IsNullOrWhiteSpace(memoryType))
                c.Parameters.AddWithValue("memoryType", memoryType);
        }

        // Count query
        await using var countCmd = new NpgsqlCommand($@"
            SELECT COUNT(*) FROM memories
            WHERE archetype IN (0, 1)
              AND {tagFilter}{ownerClause}{typeClause}", conn);
        AddTagParams(countCmd);
        AddOwnerParams(countCmd);
        AddTypeParam(countCmd);
        var countResult = await countCmd.ExecuteScalarAsync(cancellationToken);
        var totalCount = Convert.ToInt32(countResult);

        // Paginated results
        await using var cmd = new NpgsqlCommand($@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence,
                   created_at, updated_at, title, current_version, owner_type, owner_id, archetype
            FROM memories
            WHERE archetype IN (0, 1)
              AND {tagFilter}{ownerClause}{typeClause}
            ORDER BY updated_at DESC
            LIMIT @limit OFFSET @offset", conn);
        AddTagParams(cmd);
        AddOwnerParams(cmd);
        AddTypeParam(cmd);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        var results = new List<Memorizer.Models.Memory>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadMemoryFromReader(reader, withSimilarity: false));
        }

        return (results, totalCount);
    }

    // ===== Reader Helpers for New Entities =====

    private static Workspace ReadWorkspaceFromReader(NpgsqlDataReader reader)
    {
        return new Workspace
        {
            Id = new WorkspaceId(reader.GetGuid(0)),
            ParentId = reader.IsDBNull(1) ? null : new WorkspaceId(reader.GetGuid(1)),
            Name = reader.GetString(2),
            Slug = reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            IsSystem = reader.GetBoolean(5),
            Settings = reader.IsDBNull(6) ? null : reader.GetFieldValue<JsonDocument>(6),
            CreatedAt = reader.GetDateTime(7),
            UpdatedAt = reader.GetDateTime(8)
        };
    }

    private static Project ReadProjectFromReader(NpgsqlDataReader reader)
    {
        return new Project
        {
            Id = new ProjectId(reader.GetGuid(0)),
            WorkspaceId = new WorkspaceId(reader.GetGuid(1)),
            ParentId = reader.IsDBNull(2) ? null : new ProjectId(reader.GetGuid(2)),
            Name = reader.GetString(3),
            Slug = reader.GetString(4),
            Description = reader.IsDBNull(5) ? null : reader.GetString(5),
            Status = (ProjectStatusEnum)reader.GetInt16(6),
            VictoryConditions = reader.IsDBNull(7) ? null : reader.GetString(7),
            Settings = reader.IsDBNull(8) ? null : reader.GetFieldValue<JsonDocument>(8),
            CreatedAt = reader.GetDateTime(9),
            UpdatedAt = reader.GetDateTime(10),
            CompletedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11)
        };
    }

    /// <summary>
    /// Reads a Memory using the legacy 'type' column (now type_legacy).
    /// Columns: id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version
    /// </summary>
    private static Memorizer.Models.Memory ReadMemoryFromReaderLegacy(NpgsqlDataReader reader)
    {
        return new Memorizer.Models.Memory
        {
            Id = (MemoryId)reader.GetGuid(0),
            TypeLegacy = reader.IsDBNull(1) ? null : reader.GetString(1),
            Content = reader.GetFieldValue<JsonDocument>(2),
            Text = reader.GetString(3),
            Source = reader.GetString(4),
            Embedding = reader.IsDBNull(5) ? null : reader.GetFieldValue<Vector>(5),
            EmbeddingMetadata = reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector>(6),
            Tags = reader.GetFieldValue<string[]>(7),
            Confidence = new Confidence(reader.GetDouble(8)),
            CreatedAt = reader.GetDateTime(9),
            UpdatedAt = reader.GetDateTime(10),
            Title = reader.IsDBNull(11) ? null : reader.GetString(11),
            CurrentVersion = new VersionNumber(reader.GetInt32(12))
        };
    }

    /// <summary>
    /// Generates a URL-safe slug from a name.
    /// </summary>
    private static string GenerateSlug(string name)
    {
        // Convert to lowercase, replace spaces with hyphens, remove non-alphanumeric chars
        var slug = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("--", "-");

        // Remove characters that aren't alphanumeric or hyphens
        slug = Regex.Replace(slug, @"[^a-z0-9\-]", "");

        // Remove leading/trailing hyphens
        slug = slug.Trim('-');

        return string.IsNullOrEmpty(slug) ? "unnamed" : slug;
    }

    // ===== Archival Operations =====

    public async Task<Memorizer.Models.Memory?> UpdateMemoryArchetypeAsync(
        MemoryId memoryId,
        ArchetypeEnum newArchetype,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE memories
            SET archetype = @archetype, updated_at = @updatedAt
            WHERE id = @id
            RETURNING id";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);

        cmd.Parameters.AddWithValue("id", memoryId.Value);
        cmd.Parameters.AddWithValue("archetype", (short)newArchetype);
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result == null)
            return null;

        // Return the updated memory
        var updatedMemory = await Get(memoryId, cancellationToken);

        if (_markdownExportService is { IsEnabled: true } && updatedMemory != null)
        {
            try
            {
                if (newArchetype == ArchetypeEnum.Archived)
                    await _markdownExportService.DeleteMemoryFileAsync(memoryId, cancellationToken);
                else
                    await _markdownExportService.ExportMemoryAsync(updatedMemory, cancellationToken);
            }
            catch { /* Don't fail the archetype update */ }
        }

        return updatedMemory;
    }

    public async Task<(IReadOnlyList<Memorizer.Models.Memory> Memories, int TotalCount)> GetArchivedMemoriesAsync(
        int page = 1,
        int pageSize = 50,
        ProjectId? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var offset = (page - 1) * pageSize;

        // Build the WHERE clause dynamically based on whether projectId is provided
        string whereClause;
        if (projectId.HasValue)
        {
            whereClause = "archetype = 2 AND owner_type = 1 AND owner_id = @projectId";
        }
        else
        {
            whereClause = "archetype = 2";
        }

        // Query for archived memories with optional project filter
        // Column order must match ReadMemoryFromReader: id, type_legacy, content, text, source, embedding, embedding_metadata,
        // tags, confidence, created_at, updated_at, title, current_version, owner_type, owner_id, archetype
        var sql = $@"
            SELECT id, type_legacy, content, text, source, embedding, embedding_metadata, tags, confidence,
                   created_at, updated_at, title, current_version, owner_type, owner_id, archetype
            FROM memories
            WHERE {whereClause}
            ORDER BY updated_at DESC
            LIMIT @limit OFFSET @offset";

        var countSql = $@"
            SELECT COUNT(*) FROM memories WHERE {whereClause}";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get total count
        await using var countCmd = new NpgsqlCommand(countSql, connection);
        if (projectId.HasValue)
        {
            countCmd.Parameters.AddWithValue("projectId", projectId.Value.Value);
        }
        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));

        // Get paginated results
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", offset);
        if (projectId.HasValue)
        {
            cmd.Parameters.AddWithValue("projectId", projectId.Value.Value);
        }

        var memories = new List<Memorizer.Models.Memory>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            memories.Add(ReadMemoryFromReader(reader, withSimilarity: false));
        }

        return (memories, totalCount);
    }

    #region Data Migration Tracking

    public async Task<bool> HasDataMigrationRunAsync(string migrationName, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT EXISTS(SELECT 1 FROM data_migrations WHERE name = @name)", conn);

        cmd.Parameters.AddWithValue("name", migrationName);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task RecordDataMigrationAsync(string migrationName, string? description = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO data_migrations (name, description, executed_at)
            VALUES (@name, @description, NOW())
            ON CONFLICT (name) DO NOTHING", conn);

        cmd.Parameters.AddWithValue("name", migrationName);
        cmd.Parameters.AddWithValue("description", description ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ExecuteDataMigrationIfNeededAsync(
        string migrationName,
        string description,
        Func<CancellationToken, Task> migrationAction,
        CancellationToken cancellationToken = default)
    {
        // Check if migration has already run
        if (await HasDataMigrationRunAsync(migrationName, cancellationToken))
        {
            return false; // Already executed
        }

        // Execute the migration action
        await migrationAction(cancellationToken);

        // Record that the migration has been executed
        await RecordDataMigrationAsync(migrationName, description, cancellationToken);

        return true; // Migration was executed
    }

    #endregion
}
