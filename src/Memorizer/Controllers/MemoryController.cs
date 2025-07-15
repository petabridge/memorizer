using Memorizer.Models;
using Memorizer.Services;
using Microsoft.AspNetCore.Mvc;

namespace Memorizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MemoryController : ControllerBase
{
    private readonly IStorage _storage;

    public MemoryController(IStorage storage)
    {
        _storage = storage;
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