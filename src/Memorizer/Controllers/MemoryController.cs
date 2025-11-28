using Memorizer.Models;
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
    /// Get paginated list of memories
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<MemoryListResponse>> GetMemories(int page = 1, int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var (memories, totalCount) = await _storage.GetMemoriesPaginated(page, pageSize);
        
        return Ok(new MemoryListResponse
        {
            Memories = memories,
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
        var memory = await _storage.Get(id);
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
    /// Create a new memory
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Memory>> CreateMemory([FromBody] CreateMemoryRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var memory = await _storage.StoreMemory(
            request.Type,
            request.Content,
            request.Source,
            request.Tags,
            request.Confidence,
            title: request.Title
        );

        return CreatedAtAction(nameof(GetMemory), new { id = memory.Id }, memory);
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
            id,
            request.Type,
            request.Content,
            request.Source,
            request.Tags,
            request.Confidence,
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
        var success = await _storage.Delete(id);
        if (!success)
        {
            return NotFound();
        }
        return NoContent();
    }

    /// <summary>
    /// Get version history for a memory
    /// </summary>
    [HttpGet("{id}/versions")]
    public async Task<ActionResult<List<MemoryVersion>>> GetVersionHistory(Guid id, int? limit = null)
    {
        var memory = await _storage.Get(id);
        if (memory == null)
        {
            return NotFound();
        }

        var versions = await _storage.GetVersionHistory(id, limit);
        return Ok(versions);
    }

    /// <summary>
    /// Get a specific version of a memory
    /// </summary>
    [HttpGet("{id}/versions/{versionNumber}")]
    public async Task<ActionResult<MemoryVersion>> GetVersion(Guid id, int versionNumber)
    {
        var memory = await _storage.Get(id);
        if (memory == null)
        {
            return NotFound();
        }

        var version = await _storage.GetVersion(id, versionNumber);

        // If version is the current version and not stored, return live memory data
        if (version == null && versionNumber == memory.CurrentVersion)
        {
            version = new MemoryVersion
            {
                VersionId = Guid.Empty,
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
        var memory = await _storage.Get(id);
        if (memory == null)
        {
            return NotFound();
        }

        var events = await _storage.GetEvents(id, limit);
        return Ok(events);
    }

    /// <summary>
    /// Revert a memory to a specific version
    /// </summary>
    [HttpPost("{id}/versions/{versionNumber}/revert")]
    public async Task<ActionResult<Memory>> RevertToVersion(Guid id, int versionNumber, [FromQuery] string? changedBy = null)
    {
        var memory = await _storage.Get(id);
        if (memory == null)
        {
            return NotFound();
        }

        var revertedMemory = await _storage.RevertToVersion(id, versionNumber, changedBy);
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
        var memory = await _storage.Get(id);
        if (memory == null)
        {
            return NotFound();
        }

        var fromVersionObj = await _storage.GetVersion(id, fromVersion);
        var toVersionObj = await _storage.GetVersion(id, toVersion);

        if (fromVersionObj == null)
        {
            return NotFound($"Version {fromVersion} not found");
        }

        // If toVersion is the current version and not stored, use live memory data
        if (toVersionObj == null && toVersion == memory.CurrentVersion)
        {
            toVersionObj = new MemoryVersion
            {
                VersionId = Guid.Empty,
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
            ConfidenceChanged = Math.Abs(fromVersionObj.Confidence - toVersionObj.Confidence) > 0.001
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
    public async Task<ActionResult<List<Memory>>> SearchMemories(
        [FromQuery] string query,
        [FromQuery] double minSimilarity = 0.7,
        [FromQuery] int limit = 10,
        [FromQuery] string[]? filterTags = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query is required.");
        // Use metadata embeddings by default for better keyword query performance
        var results = await _storage.SearchWithMetadataEmbedding(query, limit, minSimilarity, filterTags);
        return Ok(results);
    }

    /// <summary>
    /// Search using full content embeddings (current approach)
    /// </summary>
    [HttpGet("search/full")]
    public async Task<ActionResult<List<Memory>>> SearchWithFullEmbedding(
        [FromQuery] string query,
        [FromQuery] double minSimilarity = 0.7,
        [FromQuery] int limit = 10,
        [FromQuery] string[]? filterTags = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query is required.");
        var results = await _storage.SearchWithFullEmbedding(query, limit, minSimilarity, filterTags);
        return Ok(results);
    }

    /// <summary>
    /// Search using metadata-only embeddings (PoC approach)
    /// </summary>
    [HttpGet("search/metadata")]
    public async Task<ActionResult<List<Memory>>> SearchWithMetadataEmbedding(
        [FromQuery] string query,
        [FromQuery] double minSimilarity = 0.7,
        [FromQuery] int limit = 10,
        [FromQuery] string[]? filterTags = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query is required.");
        var results = await _storage.SearchWithMetadataEmbedding(query, limit, minSimilarity, filterTags);
        return Ok(results);
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
        
        var (fullResults, metadataResults) = await _storage.CompareSearchMethods(query, limit, minSimilarity, filterTags);
        
        return Ok(new SearchComparisonResponse
        {
            Query = query,
            MinSimilarity = minSimilarity,
            Limit = limit,
            FullEmbeddingResults = fullResults,
            MetadataEmbeddingResults = metadataResults,
            Summary = new ComparisonSummary
            {
                FullEmbeddingCount = fullResults.Count,
                MetadataEmbeddingCount = metadataResults.Count,
                FullEmbeddingBestScore = fullResults.FirstOrDefault()?.Similarity,
                MetadataEmbeddingBestScore = metadataResults.FirstOrDefault()?.Similarity,
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
        var memory = await _storage.Get(id);
        if (memory == null)
        {
            return NotFound();
        }

        if (request.VersionsToKeep < 1)
        {
            return BadRequest("Must keep at least 1 version");
        }

        var purged = await _storage.PurgeVersionsKeepingLatest(id, request.VersionsToKeep);
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
        var memory = await _storage.Get(id);
        if (memory == null)
        {
            return NotFound();
        }

        // Use configured defaults if not specified
        var effectiveThreshold = threshold ?? _similaritySettings.DefaultThreshold;
        var effectiveLimit = limit ?? _similaritySettings.DefaultLimit;

        // Clamp threshold to configured bounds
        effectiveThreshold = Math.Clamp(effectiveThreshold, _similaritySettings.MinThreshold, _similaritySettings.MaxThreshold);

        var similarMemories = await _storage.GetSimilarMemories(id, effectiveThreshold, effectiveLimit);

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
        var sourceMemory = await _storage.Get(id);
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
            var targetMemory = await _storage.Get(rel.TargetMemoryId);
            if (targetMemory == null)
            {
                errors.Add($"Target memory {rel.TargetMemoryId} not found");
                continue;
            }

            // Create bidirectional relationships
            // Source -> Target
            var forwardRel = await _storage.CreateRelationship(id, rel.TargetMemoryId, "similar-to", rel.Score);
            createdRelationships.Add(forwardRel);

            // Target -> Source (same score, bidirectional)
            var backwardRel = await _storage.CreateRelationship(rel.TargetMemoryId, id, "similar-to", rel.Score);
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

public class MemoryListResponse
{
    public List<Memory> Memories { get; set; } = [];
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

public class SearchComparisonResponse
{
    public string Query { get; set; } = string.Empty;
    public double MinSimilarity { get; set; }
    public int Limit { get; set; }
    public List<Memory> FullEmbeddingResults { get; set; } = [];
    public List<Memory> MetadataEmbeddingResults { get; set; } = [];
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