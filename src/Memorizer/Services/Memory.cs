using System.Text.Json;
using System.Text.Json.Serialization;
using Memorizer.Models;
using Memorizer.Settings;
using Npgsql;
using Pgvector;
using Registrator.Net;
using MemoryRelationship = Memorizer.Models.MemoryRelationship;
using MemoryEvent = Memorizer.Models.MemoryEvent;
using MemoryVersion = Memorizer.Models.MemoryVersion;

namespace Memorizer.Services;

public interface IStorage
{
    Task<Memorizer.Models.Memory> StoreMemory(
        string type,
        string content,
        string source,
        string[]? tags,
        double confidence,
        string title,
        Guid? relatedTo = null,
        string? relationshipType = null,
        CancellationToken cancellationToken = default
    );

    Task<List<Memorizer.Models.Memory>> Search(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    );

    Task<Memorizer.Models.Memory?> Get(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<bool> Delete(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<List<Memorizer.Models.Memory>> GetMany(IEnumerable<Guid> ids, CancellationToken cancellationToken = default);
    Task<MemoryRelationship> CreateRelationship(Guid fromId, Guid toId, string type, CancellationToken cancellationToken = default);
    Task<List<MemoryRelationship>> GetRelationships(Guid memoryId, string? type = null, CancellationToken cancellationToken = default);
    
    // Pagination support
    Task<(List<Memorizer.Models.Memory> Memories, int TotalCount)> GetMemoriesPaginated(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default
    );
    
    // Update existing memory
    Task<Memorizer.Models.Memory?> UpdateMemory(
        Guid id,
        string type,
        string content,
        string source,
        string[]? tags,
        double confidence,
        string? title = null,
        CancellationToken cancellationToken = default
    );
    
    // Get distinct memory types
    Task<List<string>> GetDistinctMemoryTypes(CancellationToken cancellationToken = default);
    
    // Title generation support
    Task<List<Memorizer.Models.Memory>> GetMemoriesWithoutTitles(
        int limit = 50,
        CancellationToken cancellationToken = default
    );
    
    Task UpdateMemoryTitle(
        Guid id,
        string title,
        CancellationToken cancellationToken = default
    );

    // Metadata embedding support
    Task<int> CountMemoriesWithoutMetadataEmbeddings(CancellationToken cancellationToken = default);
    Task<List<Memorizer.Models.Memory>> GetMemoriesWithoutMetadataEmbeddings(int limit, bool includeExisting = false, CancellationToken cancellationToken = default);
    Task UpdateMemoryMetadataEmbedding(Guid memoryId, Vector embedding, CancellationToken cancellationToken = default);

    // Combined embedding update (for re-embedding when dimensions change)
    Task UpdateMemoryEmbeddings(Guid memoryId, Vector contentEmbedding, Vector metadataEmbedding, CancellationToken cancellationToken = default);

    // Dual embedding comparison methods for PoC
    Task<List<Memorizer.Models.Memory>> SearchWithFullEmbedding(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    );
    
    Task<List<Memorizer.Models.Memory>> SearchWithMetadataEmbedding(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    );
    
    Task<(List<Memorizer.Models.Memory> FullResults, List<Memorizer.Models.Memory> MetadataResults)> CompareSearchMethods(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    );

    // Versioning support
    Task<List<MemoryEvent>> GetEvents(Guid memoryId, int? limit = null, CancellationToken cancellationToken = default);
    Task<List<MemoryVersion>> GetVersionHistory(Guid memoryId, int? limit = null, CancellationToken cancellationToken = default);
    Task<MemoryVersion?> GetVersion(Guid memoryId, int versionNumber, CancellationToken cancellationToken = default);
    Task<Memorizer.Models.Memory?> RevertToVersion(Guid memoryId, int versionNumber, string? changedBy = null, CancellationToken cancellationToken = default);

    // Admin version operations
    Task<int> PurgeVersionsKeepingLatest(Guid memoryId, int versionsToKeep, CancellationToken cancellationToken = default);
    Task<int> PurgeVersionsOlderThan(DateTime cutoffDate, CancellationToken cancellationToken = default);
    Task<VersionStats> GetVersionStats(CancellationToken cancellationToken = default);
}

[AutoRegisterInterfaces(ServiceLifetime.Singleton)]
public class Storage : IStorage
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService _embeddingService;
    private readonly IDiffService _diffService;
    private readonly VersioningSettings _versioningSettings;

    public Storage(NpgsqlDataSource dataSource, IEmbeddingService embeddingService, IDiffService diffService, VersioningSettings versioningSettings)
    {
        _dataSource = dataSource;
        _embeddingService = embeddingService;
        _diffService = diffService;
        _versioningSettings = versioningSettings;
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
        double confidence,
        string title,
        Guid? relatedTo = null,
        string? relationshipType = null,
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

        Memorizer.Models.Memory memory = new()
        {
            Id = Guid.NewGuid(),
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
            Title = title // Set the title
        };

        const string sql =
            @"
            INSERT INTO memories (id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title)
            VALUES (@id, @type, @content, @text, @source, @embedding, @embeddingMetadata, @tags, @confidence, @createdAt, @updatedAt, @title)";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", memory.Id);
        cmd.Parameters.AddWithValue("type", memory.Type);
        cmd.Parameters.AddWithValue("content", memory.Content);
        cmd.Parameters.AddWithValue("text", memory.Text);
        cmd.Parameters.AddWithValue("source", memory.Source);
        cmd.Parameters.AddWithValue("embedding", memory.Embedding);
        cmd.Parameters.AddWithValue("embeddingMetadata", memory.EmbeddingMetadata);
        cmd.Parameters.AddWithValue("tags", memory.Tags ?? []);
        cmd.Parameters.AddWithValue("confidence", memory.Confidence);
        cmd.Parameters.AddWithValue("createdAt", memory.CreatedAt);
        cmd.Parameters.AddWithValue("updatedAt", memory.UpdatedAt);
        cmd.Parameters.AddWithValue("title", (object?)memory.Title ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Optionally create a relationship
        if (relatedTo.HasValue && !string.IsNullOrWhiteSpace(relationshipType))
        {
            await CreateRelationship(memory.Id, relatedTo.Value, relationshipType, cancellationToken);
        }

        return memory;
    }

    public async Task<List<Memorizer.Models.Memory>> Search(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    )
    {
        // Generate embedding for the query
        float[] queryEmbedding = await _embeddingService.Generate(
            query,
            cancellationToken
        );

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Always fetch up to 2x the requested limit for post-filtering/boosting
        int fetchLimit = limit * 2;
        string sql =
            @"
            SELECT id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, embedding <=> @embedding AS similarity
            FROM memories
            WHERE embedding <=> @embedding < @maxDistance
            ORDER BY embedding <=> @embedding LIMIT @limit";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("maxDistance", 1 - minSimilarity);
        cmd.Parameters.AddWithValue("limit", fetchLimit);

        List<Memorizer.Models.Memory> memories = [];
        List<Guid> memoryIds = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = new Memorizer.Models.Memory
            {
                Id = reader.GetGuid(0),
                Type = reader.GetString(1),
                Content = reader.GetFieldValue<JsonDocument>(2),
                Text = reader.GetString(3),
                Source = reader.GetString(4),
                Embedding = reader.GetFieldValue<Vector>(5),
                EmbeddingMetadata = reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector>(6),
                Tags = reader.GetFieldValue<string[]>(7),
                Confidence = reader.GetDouble(8),
                CreatedAt = reader.GetDateTime(9),
                UpdatedAt = reader.GetDateTime(10),
                Title = reader.IsDBNull(11) ? null : reader.GetString(11),
                CurrentVersion = reader.GetInt32(12),
                Similarity = reader.IsDBNull(13) ? null : reader.GetDouble(13)
            };
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
            double score = m.Similarity ?? 1.0;
            bool tagMatch = false;
            if (normalizedFilterTags.Count > 0 && m.Tags != null)
            {
                tagMatch = m.Tags.Select(NormalizeTag).Any(t => normalizedFilterTags.Contains(t));
                if (tagMatch) score -= tagBoost; // Lower distance = higher similarity
            }
            return (Memory: m, Score: score, TagMatch: tagMatch);
        });

        // Sort by boosted score (lower is better), then by original similarity
        var sorted = scored.OrderBy(x => x.Score).ThenBy(x => x.Memory.Similarity).Take(limit).Select(x => x.Memory).ToList();
        return sorted;
    }

    public async Task<Memorizer.Models.Memory?> Get(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql =
            @"
            SELECT id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version
            FROM memories
            WHERE id = @id";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var memory = new Memorizer.Models.Memory
            {
                Id = reader.GetGuid(0),
                Type = reader.GetString(1),
                Content = reader.GetFieldValue<JsonDocument>(2),
                Text = reader.GetString(3),
                Source = reader.GetString(4),
                Embedding = reader.GetFieldValue<Vector>(5),
                EmbeddingMetadata = reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector>(6),
                Tags = reader.GetFieldValue<string[]>(7),
                Confidence = reader.GetDouble(8),
                CreatedAt = reader.GetDateTime(9),
                UpdatedAt = reader.GetDateTime(10),
                Title = reader.IsDBNull(11) ? null : reader.GetString(11),
                CurrentVersion = reader.GetInt32(12)
            };
            // Fetch relationships for this memory
            memory.Relationships = await GetRelationships(memory.Id, null, cancellationToken);
            return memory;
        }

        return null;
    }

    public async Task<bool> Delete(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = "DELETE FROM memories WHERE id = @id";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", id);

        int rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    public async Task<List<Memorizer.Models.Memory>> GetMany(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = @"
            SELECT id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version
            FROM memories
            WHERE id = ANY(@ids)";
        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        List<Memorizer.Models.Memory> memories = [];
        List<Guid> memoryIds = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = new Memorizer.Models.Memory
            {
                Id = reader.GetGuid(0),
                Type = reader.GetString(1),
                Content = reader.GetFieldValue<JsonDocument>(2),
                Text = reader.GetString(3),
                Source = reader.GetString(4),
                Embedding = reader.GetFieldValue<Vector>(5),
                EmbeddingMetadata = reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector>(6),
                Tags = reader.GetFieldValue<string[]>(7),
                Confidence = reader.GetDouble(8),
                CreatedAt = reader.GetDateTime(9),
                UpdatedAt = reader.GetDateTime(10),
                Title = reader.IsDBNull(11) ? null : reader.GetString(11),
                CurrentVersion = reader.GetInt32(12)
            };
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

    public async Task<MemoryRelationship> CreateRelationship(Guid fromId, Guid toId, string type, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = @"
            INSERT INTO memory_relationships (id, from_memory_id, to_memory_id, type, created_at)
            VALUES (@id, @from, @to, @type, @createdAt)";
        var rel = new MemoryRelationship
        {
            Id = Guid.NewGuid(),
            FromMemoryId = fromId,
            ToMemoryId = toId,
            Type = type,
            CreatedAt = DateTime.UtcNow
        };
        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", rel.Id);
        cmd.Parameters.AddWithValue("from", rel.FromMemoryId);
        cmd.Parameters.AddWithValue("to", rel.ToMemoryId);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("createdAt", rel.CreatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rel;
    }

    public async Task<List<MemoryRelationship>> GetRelationships(Guid memoryId, string? type = null, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        
        // Single query with JOIN to get relationships and related memory titles/types - NO RECURSION!
        string sql = @"
            SELECT r.id, r.from_memory_id, r.to_memory_id, r.type, r.created_at,
                   m.title as related_title, m.type as related_type
            FROM memory_relationships r
            LEFT JOIN memories m ON r.to_memory_id = m.id
            WHERE r.from_memory_id = @id";
            
        if (!string.IsNullOrEmpty(type))
            sql += " AND r.type = @type";
            
        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", memoryId);
        if (!string.IsNullOrEmpty(type))
            cmd.Parameters.AddWithValue("type", type);
            
        List<MemoryRelationship> rels = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        
        while (await reader.ReadAsync(cancellationToken))
        {
            var rel = new MemoryRelationship
            {
                Id = reader.GetGuid(0),
                FromMemoryId = reader.GetGuid(1),
                ToMemoryId = reader.GetGuid(2),
                Type = reader.GetString(3),
                CreatedAt = reader.GetDateTime(4),
                RelatedMemoryTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                RelatedMemoryType = reader.IsDBNull(6) ? null : reader.GetString(6)
            };
            rels.Add(rel);
        }
        
        return rels;
    }

    public async Task<(List<Memorizer.Models.Memory> Memories, int TotalCount)> GetMemoriesPaginated(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        
        // Get total count first
        const string countSql = "SELECT COUNT(*) FROM memories";
        await using NpgsqlCommand countCmd = new(countSql, connection);
        var countResult = await countCmd.ExecuteScalarAsync(cancellationToken);
        var totalCount = countResult is null ? 0L : Convert.ToInt64(countResult);
        
        // Get paginated results
        const string sql = @"
            SELECT id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version
            FROM memories
            ORDER BY created_at DESC
            LIMIT @limit OFFSET @offset";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        List<Memorizer.Models.Memory> memories = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = new Memorizer.Models.Memory
            {
                Id = reader.GetGuid(0),
                Type = reader.GetString(1),
                Content = reader.GetFieldValue<JsonDocument>(2),
                Text = reader.GetString(3),
                Source = reader.GetString(4),
                Embedding = reader.GetFieldValue<Vector>(5),
                EmbeddingMetadata = reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector>(6),
                Tags = reader.GetFieldValue<string[]>(7),
                Confidence = reader.GetDouble(8),
                CreatedAt = reader.GetDateTime(9),
                UpdatedAt = reader.GetDateTime(10),
                Title = reader.IsDBNull(11) ? null : reader.GetString(11),
                CurrentVersion = reader.GetInt32(12)
            };
            // Fetch relationships for this memory
            memory.Relationships = await GetRelationships(memory.Id, null, cancellationToken);
            memories.Add(memory);
        }

        return (memories, (int)totalCount);
    }

    public async Task<Memorizer.Models.Memory?> UpdateMemory(
        Guid id,
        string type,
        string content,
        string source,
        string[]? tags,
        double confidence,
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
                SET type = @type, content = @content, text = @text, source = @source,
                    embedding = @embedding, embedding_metadata = @embeddingMetadata, tags = @tags, confidence = @confidence,
                    updated_at = @updatedAt, title = @title, current_version = @currentVersion
                WHERE id = @id";

            await using NpgsqlCommand cmd = new(sql, connection, transaction);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("type", type);
            cmd.Parameters.AddWithValue("content", document);
            cmd.Parameters.AddWithValue("text", bodyText); // Store original content, not the embedding text
            cmd.Parameters.AddWithValue("source", source);
            cmd.Parameters.AddWithValue("embedding", new Vector(embedding));
            cmd.Parameters.AddWithValue("embeddingMetadata", new Vector(embeddingMetadata));
            cmd.Parameters.AddWithValue("tags", tags ?? []);
            cmd.Parameters.AddWithValue("confidence", confidence);
            cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("title", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("currentVersion", newVersionNumber);

            int rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

            // Prune old versions if we exceed the limit
            await PruneOldVersionsIfNeeded(id, connection, transaction, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            // Return the updated memory if successful
            if (rowsAffected > 0)
            {
                return await Get(id, cancellationToken);
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
    private async Task<List<MemoryRelationship>> GetRelationshipsForMany(IEnumerable<Guid> memoryIds, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        
        // Single query to get relationships with related memory titles/types - NO RECURSION!
        const string sql = @"
            SELECT r.id, r.from_memory_id, r.to_memory_id, r.type, r.created_at,
                   m.title as related_title, m.type as related_type
            FROM memory_relationships r
            LEFT JOIN memories m ON r.to_memory_id = m.id
            WHERE r.from_memory_id = ANY(@ids)";
            
        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("ids", memoryIds.ToArray());
        
        List<MemoryRelationship> rels = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        
        while (await reader.ReadAsync(cancellationToken))
        {
            var rel = new MemoryRelationship
            {
                Id = reader.GetGuid(0),
                FromMemoryId = reader.GetGuid(1),
                ToMemoryId = reader.GetGuid(2),
                Type = reader.GetString(3),
                CreatedAt = reader.GetDateTime(4),
                RelatedMemoryTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                RelatedMemoryType = reader.IsDBNull(6) ? null : reader.GetString(6)
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
            SELECT id, type, content, text, source, embedding, tags, confidence, created_at, updated_at, title, current_version
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
            var memory = new Memorizer.Models.Memory
            {
                Id = reader.GetGuid(0),
                Type = reader.GetString(1),
                Content = reader.GetFieldValue<JsonDocument>(2),
                Text = reader.GetString(3),
                Source = reader.GetString(4),
                Embedding = reader.GetFieldValue<Vector>(5),
                Tags = reader.GetFieldValue<string[]>(6),
                Confidence = reader.GetDouble(7),
                CreatedAt = reader.GetDateTime(8),
                UpdatedAt = reader.GetDateTime(9),
                Title = reader.IsDBNull(10) ? null : reader.GetString(10),
                CurrentVersion = reader.GetInt32(11)
            };
            memories.Add(memory);
        }

        return memories;
    }

    public async Task UpdateMemoryTitle(
        Guid id,
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
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("title", title);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // Dual embedding comparison methods for PoC
    public async Task<List<Memorizer.Models.Memory>> SearchWithFullEmbedding(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    )
    {
        // Generate embedding for the query
        float[] queryEmbedding = await _embeddingService.Generate(
            query,
            cancellationToken
        );

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Always fetch up to 2x the requested limit for post-filtering/boosting
        int fetchLimit = limit * 2;
        string sql =
            @"
            SELECT id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, embedding <=> @embedding AS similarity
            FROM memories
            WHERE embedding <=> @embedding < @maxDistance
            ORDER BY embedding <=> @embedding LIMIT @limit";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("maxDistance", 1 - minSimilarity);
        cmd.Parameters.AddWithValue("limit", fetchLimit);

        List<Memorizer.Models.Memory> memories = [];
        List<Guid> memoryIds = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = new Memorizer.Models.Memory
            {
                Id = reader.GetGuid(0),
                Type = reader.GetString(1),
                Content = reader.GetFieldValue<JsonDocument>(2),
                Text = reader.GetString(3),
                Source = reader.GetString(4),
                Embedding = reader.GetFieldValue<Vector>(5),
                EmbeddingMetadata = reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector>(6),
                Tags = reader.GetFieldValue<string[]>(7),
                Confidence = reader.GetDouble(8),
                CreatedAt = reader.GetDateTime(9),
                UpdatedAt = reader.GetDateTime(10),
                Title = reader.IsDBNull(11) ? null : reader.GetString(11),
                CurrentVersion = reader.GetInt32(12),
                Similarity = reader.IsDBNull(13) ? null : reader.GetDouble(13)
            };
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
            double score = m.Similarity ?? 1.0;
            bool tagMatch = false;
            if (normalizedFilterTags.Count > 0 && m.Tags != null)
            {
                tagMatch = m.Tags.Select(NormalizeTag).Any(t => normalizedFilterTags.Contains(t));
                if (tagMatch) score -= tagBoost; // Lower distance = higher similarity
            }
            return (Memory: m, Score: score, TagMatch: tagMatch);
        });

        // Sort by boosted score (lower is better), then by original similarity
        var sorted = scored.OrderBy(x => x.Score).ThenBy(x => x.Memory.Similarity).Take(limit).Select(x => x.Memory).ToList();
        return sorted;
    }

    public async Task<List<Memorizer.Models.Memory>> SearchWithMetadataEmbedding(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    )
    {
        // Generate embedding for the query
        float[] queryEmbedding = await _embeddingService.Generate(
            query,
            cancellationToken
        );

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Always fetch up to 2x the requested limit for post-filtering/boosting
        int fetchLimit = limit * 2;
        string sql =
            @"
            SELECT id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version, embedding_metadata <=> @embedding AS similarity
            FROM memories
            WHERE embedding_metadata <=> @embedding < @maxDistance
            ORDER BY embedding_metadata <=> @embedding LIMIT @limit";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("maxDistance", 1 - minSimilarity);
        cmd.Parameters.AddWithValue("limit", fetchLimit);

        List<Memorizer.Models.Memory> memories = [];
        List<Guid> memoryIds = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = new Memory
            {
                Id = reader.GetGuid(0),
                Type = reader.GetString(1),
                Content = reader.GetFieldValue<JsonDocument>(2),
                Text = reader.GetString(3),
                Source = reader.GetString(4),
                Embedding = reader.GetFieldValue<Vector>(5),
                EmbeddingMetadata = reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector>(6),
                Tags = reader.GetFieldValue<string[]>(7),
                Confidence = reader.GetDouble(8),
                CreatedAt = reader.GetDateTime(9),
                UpdatedAt = reader.GetDateTime(10),
                Title = reader.IsDBNull(11) ? null : reader.GetString(11),
                CurrentVersion = reader.GetInt32(12),
                Similarity = reader.IsDBNull(13) ? null : reader.GetDouble(13)
            };
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
            double score = m.Similarity ?? 1.0;
            bool tagMatch = false;
            if (normalizedFilterTags.Count > 0 && m.Tags != null)
            {
                tagMatch = m.Tags.Select(NormalizeTag).Any(t => normalizedFilterTags.Contains(t));
                if (tagMatch) score -= tagBoost; // Lower distance = higher similarity
            }
            return (Memory: m, Score: score, TagMatch: tagMatch);
        });

        // Sort by boosted score (lower is better), then by original similarity
        var sorted = scored.OrderBy(x => x.Score).ThenBy(x => x.Memory.Similarity).Take(limit).Select(x => x.Memory).ToList();
        return sorted;
    }

    public async Task<(List<Memory> FullResults, List<Memory> MetadataResults)> CompareSearchMethods(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    )
    {
        var fullEmbeddingResults = await SearchWithFullEmbedding(query, limit, minSimilarity, filterTags, cancellationToken);
        var metadataEmbeddingResults = await SearchWithMetadataEmbedding(query, limit, minSimilarity, filterTags, cancellationToken);
        return (fullEmbeddingResults, metadataEmbeddingResults);
    }

    // Metadata embedding support
    public async Task<int> CountMemoriesWithoutMetadataEmbeddings(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT COUNT(*) FROM memories WHERE embedding_metadata IS NULL";
        
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task<List<Memory>> GetMemoriesWithoutMetadataEmbeddings(int limit, bool includeExisting = false, CancellationToken cancellationToken = default)
    {
        var whereClause = includeExisting ? "" : "WHERE embedding_metadata IS NULL";
        var sql = $@"
            SELECT id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version
            FROM memories
            {whereClause}
            ORDER BY created_at ASC
            LIMIT @limit";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);

        var memories = new List<Memory>();
        var memoryIds = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = new Memory
            {
                Id = reader.GetGuid(0),
                Type = reader.GetString(1),
                Content = reader.GetFieldValue<JsonDocument>(2),
                Text = reader.GetString(3),
                Source = reader.GetString(4),
                Embedding = reader.IsDBNull(5) ? new Vector(new float[384]) : reader.GetFieldValue<Vector>(5),
                EmbeddingMetadata = reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector?>(6),
                Tags = reader.GetFieldValue<string[]>(7),
                Confidence = reader.GetDouble(8),
                CreatedAt = reader.GetDateTime(9),
                UpdatedAt = reader.GetDateTime(10),
                Title = reader.IsDBNull(11) ? null : reader.GetString(11),
                CurrentVersion = reader.GetInt32(12)
            };
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

    public async Task UpdateMemoryMetadataEmbedding(Guid memoryId, Vector embedding, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE memories
            SET embedding_metadata = @embedding, updated_at = NOW()
            WHERE id = @id";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", memoryId);
        command.Parameters.AddWithValue("embedding", embedding);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateMemoryEmbeddings(Guid memoryId, Vector contentEmbedding, Vector metadataEmbedding, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE memories
            SET embedding = @contentEmbedding, embedding_metadata = @metadataEmbedding, updated_at = NOW()
            WHERE id = @id";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", memoryId);
        command.Parameters.AddWithValue("contentEmbedding", contentEmbedding);
        command.Parameters.AddWithValue("metadataEmbedding", metadataEmbedding);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Get distinct memory types
    public async Task<List<string>> GetDistinctMemoryTypes(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT DISTINCT type FROM memories";

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

    // ==================== VERSIONING SUPPORT ====================

    public async Task<List<MemoryEvent>> GetEvents(Guid memoryId, int? limit = null, CancellationToken cancellationToken = default)
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
        cmd.Parameters.AddWithValue("memoryId", memoryId);
        if (limit.HasValue)
            cmd.Parameters.AddWithValue("limit", limit.Value);

        var events = new List<MemoryEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new MemoryEvent
            {
                EventId = reader.GetGuid(0),
                MemoryId = reader.GetGuid(1),
                VersionNumber = reader.GetInt32(2),
                EventType = reader.GetString(3),
                EventData = reader.GetFieldValue<JsonDocument>(4),
                Timestamp = reader.GetDateTime(5),
                ChangedBy = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }

        return events;
    }

    public async Task<List<MemoryVersion>> GetVersionHistory(Guid memoryId, int? limit = null, CancellationToken cancellationToken = default)
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
        cmd.Parameters.AddWithValue("memoryId", memoryId);
        if (limit.HasValue)
            cmd.Parameters.AddWithValue("limit", limit.Value);

        var versions = new List<MemoryVersion>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(new MemoryVersion
            {
                VersionId = reader.GetGuid(0),
                MemoryId = reader.GetGuid(1),
                VersionNumber = reader.GetInt32(2),
                Type = reader.GetString(3),
                Content = reader.GetFieldValue<JsonDocument>(4),
                Text = reader.GetString(5),
                Source = reader.GetString(6),
                Tags = reader.GetFieldValue<string[]>(7),
                Confidence = reader.GetDouble(8),
                Title = reader.IsDBNull(9) ? null : reader.GetString(9),
                RelationshipIds = reader.GetFieldValue<Guid[]>(10),
                CreatedAt = reader.GetDateTime(11),
                VersionedAt = reader.GetDateTime(12)
            });
        }

        // Optionally load events for each version
        if (versions.Count > 0)
        {
            var events = await GetEvents(memoryId, null, cancellationToken);
            var eventsByVersion = events.GroupBy(e => e.VersionNumber).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var version in versions)
            {
                if (eventsByVersion.TryGetValue(version.VersionNumber, out var versionEvents))
                    version.Events = versionEvents;
            }
        }

        return versions;
    }

    public async Task<MemoryVersion?> GetVersion(Guid memoryId, int versionNumber, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT version_id, memory_id, version_number, type, content, text, source, tags, confidence, title, relationship_ids, created_at, versioned_at
            FROM memory_versions
            WHERE memory_id = @memoryId AND version_number = @versionNumber";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("memoryId", memoryId);
        cmd.Parameters.AddWithValue("versionNumber", versionNumber);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var version = new MemoryVersion
            {
                VersionId = reader.GetGuid(0),
                MemoryId = reader.GetGuid(1),
                VersionNumber = reader.GetInt32(2),
                Type = reader.GetString(3),
                Content = reader.GetFieldValue<JsonDocument>(4),
                Text = reader.GetString(5),
                Source = reader.GetString(6),
                Tags = reader.GetFieldValue<string[]>(7),
                Confidence = reader.GetDouble(8),
                Title = reader.IsDBNull(9) ? null : reader.GetString(9),
                RelationshipIds = reader.GetFieldValue<Guid[]>(10),
                CreatedAt = reader.GetDateTime(11),
                VersionedAt = reader.GetDateTime(12)
            };

            // Load events for this version
            var events = await GetEvents(memoryId, null, cancellationToken);
            version.Events = events.Where(e => e.VersionNumber == versionNumber).ToList();

            return version;
        }

        return null;
    }

    public async Task<Memorizer.Models.Memory?> RevertToVersion(Guid memoryId, int versionNumber, string? changedBy = null, CancellationToken cancellationToken = default)
    {
        // Get the target version snapshot
        var targetVersion = await GetVersion(memoryId, versionNumber, cancellationToken);
        if (targetVersion == null)
            return null;

        // Get current memory to determine new version number
        var currentMemory = await Get(memoryId, cancellationToken);
        if (currentMemory == null)
            return null;

        var newVersionNumber = currentMemory.CurrentVersion + 1;

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
            var revertEvent = new MemoryRevertedEvent(versionNumber, currentMemory.CurrentVersion);
            var (eventType, eventData) = revertEvent.Serialize();

            const string eventSql = @"
                INSERT INTO memory_events (memory_id, version_number, event_type, event_data, changed_by)
                VALUES (@memoryId, @versionNumber, @eventType, @eventData, @changedBy)";

            await using (var eventCmd = new NpgsqlCommand(eventSql, connection, transaction))
            {
                eventCmd.Parameters.AddWithValue("memoryId", memoryId);
                eventCmd.Parameters.AddWithValue("versionNumber", newVersionNumber);
                eventCmd.Parameters.AddWithValue("eventType", eventType);
                eventCmd.Parameters.AddWithValue("eventData", eventData);
                eventCmd.Parameters.AddWithValue("changedBy", (object?)changedBy ?? DBNull.Value);
                await eventCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Create snapshot of CURRENT state before reverting (for history)
            // This preserves the state at currentMemory.CurrentVersion so it can be reverted back to
            var relationshipIds = currentMemory.Relationships?.Select(r => r.Id).ToArray() ?? Array.Empty<Guid>();

            const string snapshotSql = @"
                INSERT INTO memory_versions (memory_id, version_number, type, content, text, source, tags, confidence, title, relationship_ids, created_at)
                VALUES (@memoryId, @versionNumber, @type, @content, @text, @source, @tags, @confidence, @title, @relationshipIds, @createdAt)
                ON CONFLICT (memory_id, version_number) DO NOTHING";

            await using (var snapshotCmd = new NpgsqlCommand(snapshotSql, connection, transaction))
            {
                snapshotCmd.Parameters.AddWithValue("memoryId", memoryId);
                // Use CURRENT version number - this snapshots the state BEFORE the revert
                snapshotCmd.Parameters.AddWithValue("versionNumber", currentMemory.CurrentVersion);
                // Use CURRENT memory content, not target version content
                snapshotCmd.Parameters.AddWithValue("type", currentMemory.Type);
                snapshotCmd.Parameters.AddWithValue("content", currentMemory.Content);
                snapshotCmd.Parameters.AddWithValue("text", currentMemory.Text);
                snapshotCmd.Parameters.AddWithValue("source", currentMemory.Source);
                snapshotCmd.Parameters.AddWithValue("tags", currentMemory.Tags ?? Array.Empty<string>());
                snapshotCmd.Parameters.AddWithValue("confidence", currentMemory.Confidence);
                snapshotCmd.Parameters.AddWithValue("title", (object?)currentMemory.Title ?? DBNull.Value);
                snapshotCmd.Parameters.AddWithValue("relationshipIds", relationshipIds);
                snapshotCmd.Parameters.AddWithValue("createdAt", currentMemory.CreatedAt);
                await snapshotCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Update the memories table with restored content
            const string updateSql = @"
                UPDATE memories
                SET type = @type, content = @content, text = @text, source = @source,
                    tags = @tags, confidence = @confidence, title = @title,
                    embedding = @embedding, embedding_metadata = @embeddingMetadata,
                    current_version = @currentVersion, updated_at = NOW()
                WHERE id = @id";

            await using (var updateCmd = new NpgsqlCommand(updateSql, connection, transaction))
            {
                updateCmd.Parameters.AddWithValue("id", memoryId);
                updateCmd.Parameters.AddWithValue("type", targetVersion.Type);
                updateCmd.Parameters.AddWithValue("content", targetVersion.Content);
                updateCmd.Parameters.AddWithValue("text", targetVersion.Text);
                updateCmd.Parameters.AddWithValue("source", targetVersion.Source);
                updateCmd.Parameters.AddWithValue("tags", targetVersion.Tags ?? Array.Empty<string>());
                updateCmd.Parameters.AddWithValue("confidence", targetVersion.Confidence);
                updateCmd.Parameters.AddWithValue("title", (object?)targetVersion.Title ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("embedding", new Vector(embedding));
                updateCmd.Parameters.AddWithValue("embeddingMetadata", new Vector(embeddingMetadata));
                updateCmd.Parameters.AddWithValue("currentVersion", newVersionNumber);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Prune old versions if we exceed the limit
            await PruneOldVersionsIfNeeded(memoryId, connection, transaction, cancellationToken);

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

    public async Task<int> PurgeVersionsKeepingLatest(Guid memoryId, int versionsToKeep, CancellationToken cancellationToken = default)
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
        cmd.Parameters.AddWithValue("memoryId", memoryId);
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
                (SELECT COUNT(*) FROM memories) as total_memories,
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
        var relationshipIds = memory.Relationships?.Select(r => r.Id).ToArray() ?? Array.Empty<Guid>();

        // Serialize the strongly-typed event
        var (eventType, eventDataJson) = changeEvent.Serialize();

        const string eventSql = @"
            INSERT INTO memory_events (memory_id, version_number, event_type, event_data, changed_by)
            VALUES (@memoryId, @versionNumber, @eventType, @eventData, @changedBy)";

        await using (var eventCmd = new NpgsqlCommand(eventSql, connection, transaction))
        {
            eventCmd.Parameters.AddWithValue("memoryId", memory.Id);
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
        snapshotCmd.Parameters.AddWithValue("memoryId", memory.Id);
        snapshotCmd.Parameters.AddWithValue("versionNumber", newVersionNumber);
        snapshotCmd.Parameters.AddWithValue("type", memory.Type);
        snapshotCmd.Parameters.AddWithValue("content", memory.Content);
        snapshotCmd.Parameters.AddWithValue("text", memory.Text);
        snapshotCmd.Parameters.AddWithValue("source", memory.Source);
        snapshotCmd.Parameters.AddWithValue("tags", memory.Tags ?? Array.Empty<string>());
        snapshotCmd.Parameters.AddWithValue("confidence", memory.Confidence);
        snapshotCmd.Parameters.AddWithValue("title", (object?)memory.Title ?? DBNull.Value);
        snapshotCmd.Parameters.AddWithValue("relationshipIds", relationshipIds);
        snapshotCmd.Parameters.AddWithValue("createdAt", memory.CreatedAt);
        await snapshotCmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
