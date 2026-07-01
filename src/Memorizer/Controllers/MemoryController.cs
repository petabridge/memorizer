using System.Text.Json;
using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.AspNetCore.Mvc;
using SimilarMemory = Memorizer.Models.SimilarMemory;

// ReSharper disable ClassNeverInstantiated.Global

namespace Memorizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MemoryController : ControllerBase
{
    private readonly IStorage _storage;
    private readonly SimilaritySettings _similaritySettings;

    public MemoryController(IStorage storage, SimilaritySettings similaritySettings)
    {
        _storage = storage;
        _similaritySettings = similaritySettings;
    }

    /// <summary>
    /// Get paginated list of memories, optionally filtered by workspace, project, or unfiled status
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<MemoryListResponse>> GetMemories(
        int page = 1,
        int pageSize = 20,
        [FromQuery] Guid? workspaceId = null,
        [FromQuery] Guid? projectId = null,
        [FromQuery] bool unfiled = false,
        [FromQuery] string[]? tag = null,
        [FromQuery] string? type = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        List<Memory> memories;
        int totalCount;

        // Filter out empty tags
        var validTags = tag?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];

        // Priority: tags > projectId > workspaceId > unfiled > all
        if (validTags.Length > 0)
        {
            // Tag filter can be combined with project/workspace/unfiled scope
            MemoryOwner? owner = null;
            if (projectId.HasValue)
                owner = MemoryOwner.ForProject(new ProjectId(projectId.Value));
            else if (workspaceId.HasValue)
                owner = MemoryOwner.ForWorkspace(new WorkspaceId(workspaceId.Value));
            else if (unfiled)
                owner = MemoryOwner.Unfiled;

            var (tagMemories, tagCount) = await _storage.GetMemoriesByTagAsync(validTags, page, pageSize, owner, type);
            memories = tagMemories.ToList();
            totalCount = tagCount;
        }
        else if (projectId.HasValue)
        {
            var owner = MemoryOwner.ForProject(new ProjectId(projectId.Value));
            memories = (await _storage.GetMemoriesByOwnerAsync(owner, page, pageSize, type)).ToList();
            totalCount = await _storage.GetMemoryCountByOwnerAsync(owner, type);
        }
        else if (workspaceId.HasValue)
        {
            var owner = MemoryOwner.ForWorkspace(new WorkspaceId(workspaceId.Value));
            memories = (await _storage.GetMemoriesByOwnerAsync(owner, page, pageSize, type)).ToList();
            totalCount = await _storage.GetMemoryCountByOwnerAsync(owner, type);
        }
        else if (unfiled)
        {
            memories = (await _storage.GetUnfiledMemoriesAsync(page, pageSize, type)).ToList();
            totalCount = await _storage.GetUnfiledMemoryCountAsync(type);
        }
        else
        {
            (memories, totalCount) = await _storage.GetMemoriesPaginated(page, pageSize, type);
        }

        return Ok(new MemoryListResponse
        {
            Memories = memories.Select(MemoryListItem.FromMemory).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    /// <summary>
    /// Get a specific memory by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Memory>> GetMemory(Guid id)
    {
        var memory = await _storage.Get((MemoryId)id);
        if (memory == null)
        {
            return NotFound();
        }
        return Ok(memory);
    }

    /// <summary>
    /// Get all distinct memory types
    /// </summary>
    [HttpGet("types")]
    public async Task<ActionResult<List<string>>> GetMemoryTypes()
    {
        var types = await _storage.GetDistinctMemoryTypes();
        return Ok(types);
    }

    /// <summary>
    /// Get all distinct tags across non-archived memories, optionally scoped by workspace/project
    /// </summary>
    [HttpGet("tags")]
    public async Task<ActionResult<List<string>>> GetDistinctTags(
        [FromQuery] Guid? workspaceId = null,
        [FromQuery] Guid? projectId = null)
    {
        MemoryOwner? owner = null;
        if (projectId.HasValue)
            owner = MemoryOwner.ForProject(new ProjectId(projectId.Value));
        else if (workspaceId.HasValue)
            owner = MemoryOwner.ForWorkspace(new WorkspaceId(workspaceId.Value));

        var tags = await _storage.GetDistinctTagsAsync(owner);
        return Ok(tags);
    }

    /// <summary>
    /// Get distinct owner (type, id) pairs for memories matching the given filters
    /// </summary>
    [HttpGet("owners")]
    public async Task<ActionResult<List<OwnerDto>>> GetDistinctOwners(
        [FromQuery] string[]? tag = null,
        [FromQuery] string? type = null)
    {
        var validTags = tag?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        var owners = await _storage.GetDistinctOwnersAsync(validTags, type);
        return Ok(owners.Select(o => new OwnerDto
        {
            Type = o.Type.ToString(),
            Id = o.Id
        }).ToList());
    }

    /// <summary>
    /// Create a new memory
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Memory>> CreateMemory([FromBody] CreateMemoryRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // Determine owner: project takes precedence over workspace
        MemoryOwner? owner = null;
        if (request.ProjectId.HasValue)
        {
            owner = MemoryOwner.ForProject(new ProjectId(request.ProjectId.Value));
        }
        else if (request.WorkspaceId.HasValue)
        {
            owner = MemoryOwner.ForWorkspace(new WorkspaceId(request.WorkspaceId.Value));
        }

        var memory = await _storage.StoreMemory(
            request.Type,
            request.Content,
            request.Source,
            request.Tags,
            new Confidence(request.Confidence),
            title: request.Title,
            owner: owner
        );

        return CreatedAtAction(nameof(GetMemory), new { id = memory.Id.Value }, memory);
    }

    /// <summary>
    /// Update an existing memory
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<Memory>> UpdateMemory(Guid id, [FromBody] UpdateMemoryRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var memory = await _storage.UpdateMemory(
            (MemoryId)id,
            request.Type,
            request.Content,
            request.Source,
            request.Tags,
            new Confidence(request.Confidence),
            request.Title
        );

        if (memory == null)
        {
            return NotFound();
        }

        return Ok(memory);
    }

    /// <summary>
    /// Delete a memory
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteMemory(Guid id)
    {
        var success = await _storage.Delete((MemoryId)id);
        if (!success)
        {
            return NotFound();
        }
        return NoContent();
    }

    /// <summary>
    /// Update the owner (workspace/project) of a memory
    /// </summary>
    [HttpPatch("{id}/owner")]
    public async Task<ActionResult<Memory>> UpdateMemoryOwner(Guid id, [FromBody] UpdateOwnerRequest request)
    {
        var memoryId = (MemoryId)id;
        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        // Determine new owner: project takes precedence over workspace
        MemoryOwner owner;
        if (request.ProjectId.HasValue)
        {
            owner = MemoryOwner.ForProject(new ProjectId(request.ProjectId.Value));
        }
        else if (request.WorkspaceId.HasValue)
        {
            owner = MemoryOwner.ForWorkspace(new WorkspaceId(request.WorkspaceId.Value));
        }
        else
        {
            // Neither provided - set to unfiled
            owner = MemoryOwner.Unfiled;
        }

        await _storage.UpdateMemoryOwner(memoryId, owner);

        // Fetch and return updated memory
        var updatedMemory = await _storage.Get(memoryId);
        return Ok(updatedMemory);
    }

    /// <summary>
    /// Archive a memory (mark as obsolete)
    /// </summary>
    [HttpPatch("{id}/archive")]
    public async Task<ActionResult<Memory>> ArchiveMemory(Guid id)
    {
        var memoryId = (MemoryId)id;
        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        if (memory.Archetype == ArchetypeEnum.Archived)
        {
            return BadRequest("Memory is already archived");
        }

        var archivedMemory = await _storage.UpdateMemoryArchetypeAsync(memoryId, ArchetypeEnum.Archived);
        if (archivedMemory == null)
        {
            return StatusCode(500, "Failed to archive memory");
        }

        return Ok(archivedMemory);
    }

    /// <summary>
    /// Restore an archived memory to active status
    /// </summary>
    [HttpPatch("{id}/restore")]
    public async Task<ActionResult<Memory>> RestoreMemory(Guid id, [FromBody] RestoreMemoryRequest request)
    {
        var memoryId = (MemoryId)id;
        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        if (memory.Archetype != ArchetypeEnum.Archived)
        {
            return BadRequest("Memory is not archived");
        }

        var targetArchetype = ArchetypeEnumExtensions.ParseArchetype(request.RestoreAs ?? "document");
        if (targetArchetype == ArchetypeEnum.Archived)
        {
            return BadRequest("Cannot restore to archived status");
        }

        var restoredMemory = await _storage.UpdateMemoryArchetypeAsync(memoryId, targetArchetype);
        if (restoredMemory == null)
        {
            return StatusCode(500, "Failed to restore memory");
        }

        return Ok(restoredMemory);
    }

    /// <summary>
    /// Get paginated list of archived memories
    /// </summary>
    [HttpGet("archived")]
    public async Task<ActionResult<ArchivedMemoryListResponse>> GetArchivedMemories(
        int page = 1,
        int pageSize = 20,
        [FromQuery] Guid? projectId = null,
        [FromQuery] Guid? workspaceId = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        ProjectId? typedProjectId = projectId.HasValue ? new ProjectId(projectId.Value) : null;
        var (memories, totalCount) = await _storage.GetArchivedMemoriesAsync(page, pageSize, typedProjectId);

        return Ok(new ArchivedMemoryListResponse
        {
            Memories = memories.Select(MemoryListItem.FromMemory).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    /// <summary>
    /// Get version history for a memory
    /// </summary>
    [HttpGet("{id}/versions")]
    public async Task<ActionResult<List<MemoryVersion>>> GetVersionHistory(Guid id, int? limit = null)
    {
        var memoryId = (MemoryId)id;
        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        var versions = await _storage.GetVersionHistory(memoryId, limit);
        return Ok(versions);
    }

    /// <summary>
    /// Get a specific version of a memory
    /// </summary>
    [HttpGet("{id}/versions/{versionNumber}")]
    public async Task<ActionResult<MemoryVersion>> GetVersion(Guid id, int versionNumber)
    {
        var memoryId = (MemoryId)id;
        var versionNum = new VersionNumber(versionNumber);
        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        var version = await _storage.GetVersion(memoryId, versionNum);

        // If version is the current version and not stored, return live memory data
        if (version == null && versionNum == memory.CurrentVersion)
        {
            version = new MemoryVersion
            {
                VersionId = (VersionId)Guid.Empty,
                MemoryId = memory.Id,
                VersionNumber = memory.CurrentVersion,
                Type = memory.Type,
                Content = memory.Content,
                Text = memory.Text,
                Source = memory.Source,
                Tags = memory.Tags,
                Confidence = memory.Confidence,
                Title = memory.Title,
                CreatedAt = memory.CreatedAt,
                VersionedAt = memory.UpdatedAt
            };
        }
        else if (version == null)
        {
            return NotFound($"Version {versionNumber} not found");
        }

        return Ok(version);
    }

    /// <summary>
    /// Get audit events for a memory
    /// </summary>
    [HttpGet("{id}/events")]
    public async Task<ActionResult<List<MemoryEvent>>> GetEvents(Guid id, int? limit = null)
    {
        var memoryId = (MemoryId)id;
        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        var events = await _storage.GetEvents(memoryId, limit);
        return Ok(events);
    }

    /// <summary>
    /// Revert a memory to a specific version
    /// </summary>
    [HttpPost("{id}/versions/{versionNumber}/revert")]
    public async Task<ActionResult<Memory>> RevertToVersion(Guid id, int versionNumber, [FromQuery] string? changedBy = null)
    {
        var memoryId = (MemoryId)id;
        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        var revertedMemory = await _storage.RevertToVersion(memoryId, new VersionNumber(versionNumber), changedBy);
        if (revertedMemory == null)
        {
            return NotFound($"Version {versionNumber} not found or could not be reverted");
        }

        return Ok(revertedMemory);
    }

    /// <summary>
    /// Compare two versions of a memory (diff)
    /// </summary>
    [HttpGet("{id}/versions/compare")]
    public async Task<ActionResult<VersionComparisonResponse>> CompareVersions(
        Guid id,
        [FromQuery] int fromVersion,
        [FromQuery] int toVersion,
        [FromServices] IDiffService diffService)
    {
        var memoryId = (MemoryId)id;
        var fromVersionNum = new VersionNumber(fromVersion);
        var toVersionNum = new VersionNumber(toVersion);

        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        var fromVersionObj = await _storage.GetVersion(memoryId, fromVersionNum);
        var toVersionObj = await _storage.GetVersion(memoryId, toVersionNum);

        if (fromVersionObj == null)
        {
            return NotFound($"Version {fromVersion} not found");
        }

        // If toVersion is the current version and not stored, use live memory data
        if (toVersionObj == null && toVersionNum == memory.CurrentVersion)
        {
            toVersionObj = new MemoryVersion
            {
                VersionId = (VersionId)Guid.Empty,
                MemoryId = memory.Id,
                VersionNumber = memory.CurrentVersion,
                Type = memory.Type,
                Content = memory.Content,
                Text = memory.Text,
                Source = memory.Source,
                Tags = memory.Tags,
                Confidence = memory.Confidence,
                Title = memory.Title,
                CreatedAt = memory.CreatedAt,
                VersionedAt = memory.UpdatedAt
            };
        }
        else if (toVersionObj == null)
        {
            return NotFound($"Version {toVersion} not found");
        }

        var textDiff = diffService.ComputeDiff(fromVersionObj.Text, toVersionObj.Text);

        return Ok(new VersionComparisonResponse
        {
            MemoryId = id,
            FromVersion = fromVersion,
            ToVersion = toVersion,
            FromVersionDetails = fromVersionObj,
            ToVersionDetails = toVersionObj,
            TextDiff = textDiff,
            TextDiffHtml = diffService.RenderInlineHtml(textDiff),
            TagsChanged = !TagsAreEqual(fromVersionObj.Tags, toVersionObj.Tags),
            TitleChanged = fromVersionObj.Title != toVersionObj.Title,
            TypeChanged = fromVersionObj.Type != toVersionObj.Type,
            ConfidenceChanged = Math.Abs((double)fromVersionObj.Confidence - (double)toVersionObj.Confidence) > 0.001
        });
    }

    private static bool TagsAreEqual(string[]? tags1, string[]? tags2)
    {
        if (tags1 == null && tags2 == null) return true;
        if (tags1 == null || tags2 == null) return false;
        return tags1.OrderBy(t => t).SequenceEqual(tags2.OrderBy(t => t));
    }

    /// <summary>
    /// Vector search for memories using metadata embeddings (optimized for keyword queries)
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<MemoryListItem>>> SearchMemories(
        [FromQuery] string query,
        [FromQuery] double minSimilarity = 0.7,
        [FromQuery] int limit = 10,
        [FromQuery] string[]? filterTags = null,
        [FromQuery] Guid? projectId = null,
        [FromQuery] bool includeUnassigned = false,
        [FromQuery] bool includeArchived = false,
        [FromQuery] Guid? workspaceId = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query is required.");

        if (projectId.HasValue && workspaceId.HasValue)
            return BadRequest("projectId and workspaceId are mutually exclusive search scopes.");

        ProjectId? typedProjectId = projectId.HasValue ? new ProjectId(projectId.Value) : null;
        WorkspaceId? typedWorkspaceId = workspaceId.HasValue ? new WorkspaceId(workspaceId.Value) : null;

        // Use metadata embeddings by default for better keyword query performance
        var results = await _storage.SearchWithMetadataEmbedding(
            query,
            limit,
            new SimilarityScore(minSimilarity),
            filterTags,
            typedProjectId,
            includeUnassigned,
            includeArchived,
            includeSystem: false,
            workspaceId: typedWorkspaceId);
        return Ok(results.Select(MemoryListItem.FromMemory).ToList());
    }

    /// <summary>
    /// Search using full content embeddings (current approach)
    /// </summary>
    [HttpGet("search/full")]
    public async Task<ActionResult<List<MemoryListItem>>> SearchWithFullEmbedding(
        [FromQuery] string query,
        [FromQuery] double minSimilarity = 0.7,
        [FromQuery] int limit = 10,
        [FromQuery] string[]? filterTags = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query is required.");
        var results = await _storage.SearchWithFullEmbedding(query, limit, new SimilarityScore(minSimilarity), filterTags);
        return Ok(results.Select(MemoryListItem.FromMemory).ToList());
    }

    /// <summary>
    /// Search using metadata-only embeddings (PoC approach)
    /// </summary>
    [HttpGet("search/metadata")]
    public async Task<ActionResult<List<MemoryListItem>>> SearchWithMetadataEmbedding(
        [FromQuery] string query,
        [FromQuery] double minSimilarity = 0.7,
        [FromQuery] int limit = 10,
        [FromQuery] string[]? filterTags = null,
        [FromQuery] Guid? projectId = null,
        [FromQuery] bool includeUnassigned = false,
        [FromQuery] bool includeArchived = false,
        [FromQuery] Guid? workspaceId = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query is required.");

        if (projectId.HasValue && workspaceId.HasValue)
            return BadRequest("projectId and workspaceId are mutually exclusive search scopes.");

        ProjectId? typedProjectId = projectId.HasValue ? new ProjectId(projectId.Value) : null;
        WorkspaceId? typedWorkspaceId = workspaceId.HasValue ? new WorkspaceId(workspaceId.Value) : null;

        var results = await _storage.SearchWithMetadataEmbedding(
            query,
            limit,
            new SimilarityScore(minSimilarity),
            filterTags,
            typedProjectId,
            includeUnassigned,
            includeArchived,
            includeSystem: false,
            workspaceId: typedWorkspaceId);
        return Ok(results.Select(MemoryListItem.FromMemory).ToList());
    }

    /// <summary>
    /// Compare both search methods side by side
    /// </summary>
    [HttpGet("search/compare")]
    public async Task<ActionResult<SearchComparisonResponse>> CompareSearchMethods(
        [FromQuery] string query,
        [FromQuery] double minSimilarity = 0.7,
        [FromQuery] int limit = 10,
        [FromQuery] string[]? filterTags = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query is required.");

        var (fullResults, metadataResults) = await _storage.CompareSearchMethods(query, limit, new SimilarityScore(minSimilarity), filterTags);

        var fullResultItems = fullResults.Select(MemoryListItem.FromMemory).ToList();
        var metadataResultItems = metadataResults.Select(MemoryListItem.FromMemory).ToList();

        return Ok(new SearchComparisonResponse
        {
            Query = query,
            MinSimilarity = minSimilarity,
            Limit = limit,
            FullEmbeddingResults = fullResultItems,
            MetadataEmbeddingResults = metadataResultItems,
            Summary = new ComparisonSummary
            {
                FullEmbeddingCount = fullResults.Count,
                MetadataEmbeddingCount = metadataResults.Count,
                FullEmbeddingBestScore = fullResults.FirstOrDefault()?.Similarity.HasValue == true ? (double?)fullResults.First().Similarity!.Value : null,
                MetadataEmbeddingBestScore = metadataResults.FirstOrDefault()?.Similarity.HasValue == true ? (double?)metadataResults.First().Similarity!.Value : null,
                UniqueToFull = fullResults.Count(f => metadataResults.All(m => m.Id != f.Id)),
                UniqueToMetadata = metadataResults.Count(m => fullResults.All(f => f.Id != m.Id))
            }
        });
    }

    // ==================== Per-Memory Version Management Endpoints ====================

    /// <summary>
    /// Purge old versions for a specific memory, keeping only the latest N versions
    /// </summary>
    [HttpPost("{id}/versions/purge")]
    public async Task<ActionResult<PurgeResult>> PurgeVersionsForMemory(Guid id, [FromBody] PurgeVersionsRequest request)
    {
        var memoryId = (MemoryId)id;
        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        if (request.VersionsToKeep < 1)
        {
            return BadRequest("Must keep at least 1 version");
        }

        var purged = await _storage.PurgeVersionsKeepingLatest(memoryId, request.VersionsToKeep);
        return Ok(new PurgeResult
        {
            VersionsPurged = purged,
            Message = $"Purged {purged} old version(s), keeping latest {request.VersionsToKeep}"
        });
    }

    // ==================== Similar Memory Discovery Endpoints ====================

    /// <summary>
    /// Get similarity settings (thresholds and limits) for the UI
    /// </summary>
    [HttpGet("similarity/settings")]
    public ActionResult<SimilaritySettingsResponse> GetSimilaritySettings()
    {
        return Ok(new SimilaritySettingsResponse
        {
            DefaultThreshold = _similaritySettings.DefaultThreshold,
            MinThreshold = _similaritySettings.MinThreshold,
            MaxThreshold = _similaritySettings.MaxThreshold,
            DefaultLimit = _similaritySettings.DefaultLimit
        });
    }

    /// <summary>
    /// Get memories similar to the specified memory using vector similarity search
    /// </summary>
    [HttpGet("{id}/similar")]
    public async Task<ActionResult<SimilarMemoriesResponse>> GetSimilarMemories(
        Guid id,
        [FromQuery] double? threshold = null,
        [FromQuery] int? limit = null)
    {
        var memoryId = (MemoryId)id;
        var memory = await _storage.Get(memoryId);
        if (memory == null)
        {
            return NotFound();
        }

        // Use configured defaults if not specified
        var effectiveThreshold = threshold ?? _similaritySettings.DefaultThreshold;
        var effectiveLimit = limit ?? _similaritySettings.DefaultLimit;

        // Clamp threshold to configured bounds
        effectiveThreshold = Math.Clamp(effectiveThreshold, _similaritySettings.MinThreshold, _similaritySettings.MaxThreshold);

        var similarMemories = await _storage.GetSimilarMemories(memoryId, new SimilarityScore(effectiveThreshold), effectiveLimit);

        return Ok(new SimilarMemoriesResponse
        {
            SourceMemoryId = id,
            Threshold = effectiveThreshold,
            Limit = effectiveLimit,
            SimilarMemories = similarMemories
        });
    }

    /// <summary>
    /// Create bidirectional 'similar-to' relationships between memories with similarity scores
    /// </summary>
    [HttpPost("{id}/similar")]
    public async Task<ActionResult<CreateSimilarRelationshipsResponse>> CreateSimilarRelationships(
        Guid id,
        [FromBody] CreateSimilarRelationshipsRequest request)
    {
        var memoryId = (MemoryId)id;
        var sourceMemory = await _storage.Get(memoryId);
        if (sourceMemory == null)
        {
            return NotFound();
        }

        if (request.Relationships == null || request.Relationships.Count == 0)
        {
            return BadRequest("At least one relationship must be specified");
        }

        var createdRelationships = new List<MemoryRelationship>();
        var errors = new List<string>();

        foreach (var rel in request.Relationships)
        {
            // Validate the target memory exists
            var targetMemoryId = (MemoryId)rel.TargetMemoryId;
            var targetMemory = await _storage.Get(targetMemoryId);
            if (targetMemory == null)
            {
                errors.Add($"Target memory {rel.TargetMemoryId} not found");
                continue;
            }

            var score = new SimilarityScore(rel.Score);

            // Create bidirectional relationships
            // Source -> Target
            var forwardRel = await _storage.CreateRelationship(memoryId, targetMemoryId, "similar-to", score);
            createdRelationships.Add(forwardRel);

            // Target -> Source (same score, bidirectional)
            var backwardRel = await _storage.CreateRelationship(targetMemoryId, memoryId, "similar-to", score);
            createdRelationships.Add(backwardRel);
        }

        return Ok(new CreateSimilarRelationshipsResponse
        {
            SourceMemoryId = id,
            RelationshipsCreated = createdRelationships.Count,
            Relationships = createdRelationships,
            Errors = errors
        });
    }
}

/// <summary>
/// Lightweight memory representation for list views - excludes embedding vectors
/// </summary>
public class MemoryListItem
{
    public Guid Id { get; set; }
    public string? TypeLegacy { get; set; }
    public string Type { get; set; } = string.Empty;
    public JsonDocument Content { get; set; } = JsonDocument.Parse("{}");
    public string Source { get; set; } = string.Empty;
    // Embedding and EmbeddingMetadata intentionally excluded to reduce payload size
    public string[]? Tags { get; set; }
    public double Confidence { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Title { get; set; }
    public string Text { get; set; } = string.Empty;
    public int CurrentVersion { get; set; }
    public double? Similarity { get; set; }
    public List<MemoryRelationship>? Relationships { get; set; }
    public MemoryOwner Owner { get; set; } = MemoryOwner.Unfiled;
    public string MemoryType { get; set; } = string.Empty;
    public string Archetype { get; set; } = string.Empty;

    public static MemoryListItem FromMemory(Memory memory)
    {
        return new MemoryListItem
        {
            Id = memory.Id.Value,
            TypeLegacy = memory.TypeLegacy,
            Type = memory.Type,
            Content = memory.Content,
            Source = memory.Source,
            Tags = memory.Tags,
            Confidence = memory.Confidence.Value,
            CreatedAt = memory.CreatedAt,
            UpdatedAt = memory.UpdatedAt,
            Title = memory.Title,
            Text = memory.Text,
            CurrentVersion = memory.CurrentVersion.Value,
            Similarity = memory.Similarity?.Value,
            Relationships = memory.Relationships,
            Owner = memory.Owner,
            MemoryType = memory.MemoryType.ToString().ToLowerInvariant(),
            Archetype = memory.Archetype.ToString().ToLowerInvariant()
        };
    }
}

public class MemoryListResponse
{
    public List<MemoryListItem> Memories { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

public class CreateMemoryRequest
{
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string[]? Tags { get; set; }
    public double Confidence { get; set; } = 1.0;
    public string Title { get; set; } = string.Empty;
    /// <summary>
    /// Optional workspace ID to assign the memory to
    /// </summary>
    public Guid? WorkspaceId { get; set; }
    /// <summary>
    /// Optional project ID to assign the memory to (takes precedence over WorkspaceId)
    /// </summary>
    public Guid? ProjectId { get; set; }
}

public class UpdateMemoryRequest
{
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string[]? Tags { get; set; }
    public double Confidence { get; set; } = 1.0;
    public string Title { get; set; } = string.Empty;
}

public class UpdateOwnerRequest
{
    /// <summary>
    /// Workspace ID to assign the memory to (ignored if ProjectId is set)
    /// </summary>
    public Guid? WorkspaceId { get; set; }
    /// <summary>
    /// Project ID to assign the memory to (takes precedence over WorkspaceId)
    /// </summary>
    public Guid? ProjectId { get; set; }
}

public class SearchComparisonResponse
{
    public string Query { get; set; } = string.Empty;
    public double MinSimilarity { get; set; }
    public int Limit { get; set; }
    public List<MemoryListItem> FullEmbeddingResults { get; set; } = [];
    public List<MemoryListItem> MetadataEmbeddingResults { get; set; } = [];
    public ComparisonSummary Summary { get; set; } = new();
}

public class ComparisonSummary
{
    public int FullEmbeddingCount { get; set; }
    public int MetadataEmbeddingCount { get; set; }
    public double? FullEmbeddingBestScore { get; set; }
    public double? MetadataEmbeddingBestScore { get; set; }
    public int UniqueToFull { get; set; }
    public int UniqueToMetadata { get; set; }
}

public class VersionComparisonResponse
{
    public Guid MemoryId { get; set; }
    public int FromVersion { get; set; }
    public int ToVersion { get; set; }
    public MemoryVersion FromVersionDetails { get; set; } = null!;
    public MemoryVersion ToVersionDetails { get; set; } = null!;
    public DiffResult TextDiff { get; set; } = null!;
    public string TextDiffHtml { get; set; } = string.Empty;
    public bool TagsChanged { get; set; }
    public bool TitleChanged { get; set; }
    public bool TypeChanged { get; set; }
    public bool ConfidenceChanged { get; set; }
}

public class PurgeVersionsRequest
{
    /// <summary>
    /// Number of latest versions to keep for each memory
    /// </summary>
    public int VersionsToKeep { get; set; } = 5;
}

public class PurgeResult
{
    public int VersionsPurged { get; set; }
    public string Message { get; set; } = string.Empty;
}

// ==================== Similarity Discovery DTOs ====================

public class SimilaritySettingsResponse
{
    public double DefaultThreshold { get; set; }
    public double MinThreshold { get; set; }
    public double MaxThreshold { get; set; }
    public int DefaultLimit { get; set; }
}

public class SimilarMemoriesResponse
{
    public Guid SourceMemoryId { get; set; }
    public double Threshold { get; set; }
    public int Limit { get; set; }
    public List<SimilarMemory> SimilarMemories { get; set; } = [];
}

public class CreateSimilarRelationshipsRequest
{
    /// <summary>
    /// List of relationships to create (each will be created bidirectionally)
    /// </summary>
    public List<SimilarRelationshipItem> Relationships { get; set; } = [];
}

public class SimilarRelationshipItem
{
    /// <summary>
    /// ID of the target memory to create a relationship with
    /// </summary>
    public Guid TargetMemoryId { get; set; }

    /// <summary>
    /// Similarity score (0.0 to 1.0)
    /// </summary>
    public double Score { get; set; }
}

public class CreateSimilarRelationshipsResponse
{
    public Guid SourceMemoryId { get; set; }
    public int RelationshipsCreated { get; set; }
    public List<MemoryRelationship> Relationships { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

// ==================== Archival DTOs ====================

public class RestoreMemoryRequest
{
    /// <summary>
    /// Archetype to restore to: "document" (default) or "record"
    /// </summary>
    public string? RestoreAs { get; set; } = "document";
}

public class ArchivedMemoryListResponse
{
    public List<MemoryListItem> Memories { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

public class OwnerDto
{
    public string Type { get; set; } = string.Empty;
    public Guid Id { get; set; }
}
