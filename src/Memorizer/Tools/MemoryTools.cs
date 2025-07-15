using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Memory = Memorizer.Models.Memory;
using System.Linq;
using System.Diagnostics;
using Memorizer.Services;
using Memorizer.Telemetry;
using Microsoft.Extensions.Logging;

namespace PostgMem.Tools;

[McpServerToolType]
public class MemoryTools
{
    private readonly IStorage _storage;
    private readonly ILogger<MemoryTools> _logger;

    public MemoryTools(IStorage storage, ILogger<MemoryTools> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    [McpServerTool, Description("Store a new memory in the database, optionally creating a relationship to another memory. Use this to save reference material, how-to guides, coding standards, or any information you (the LLM) may want to refer to when completing tasks. Include as much context as possible, such as markdown, code samples, and detailed explanations. Create relationships to link related reference materials or examples.")]
    public async Task<string> Store(
        [Description("The type of memory (e.g., 'conversation', 'document', 'reference', 'how-to', etc.). Use 'reference' or 'how-to' for reusable knowledge.")] string type,
        [Description("Plain text (markdown, code, prose, etc.) that you want to store and embed.")] string text,
        [Description("The source of the memory (e.g., 'user', 'system', 'LLM', etc.). Use 'LLM' if you are storing knowledge for your own future use.")] string source,
        [Description("Title for the memory. This is required and must not be null or empty.")] string title,
        [Description("Optional tags to categorize the memory. Use tags like 'coding-standard', 'unit-test', 'reference', 'how-to', etc. to make retrieval easier.")] string[]? tags = null,
        [Description("Confidence score for the memory (0.0 to 1.0)")] double confidence = 1.0,
        [Description("Optionally, the ID of a related memory. Use this to link related reference materials, how-tos, or examples.")] Guid? relatedTo = null,
        [Description("Optionally, the type of relationship to create (e.g., 'example-of', 'explains', 'related-to'). Use relationships to connect related knowledge.")] string? relationshipType = null,
        CancellationToken cancellationToken = default
    )
    {
        Memory memory = await _storage.StoreMemory(
            type,
            text,
            source,
            tags,
            confidence,
            title: title,
            cancellationToken: cancellationToken
        );

        // Handle manual relationship creation if specified
        if (relatedTo.HasValue && !string.IsNullOrWhiteSpace(relationshipType))
        {
            await _storage.CreateRelationship(memory.Id, relatedTo.Value, relationshipType, cancellationToken);
        }

        return $"Memory stored successfully with ID: {memory.Id}. You might want to call `CreateRelationship` to associate this memory with another memory for better context retrieval.";
    }

    [McpServerTool, Description("Search for memories similar to the provided text. Use this to retrieve reference material, how-tos, or examples relevant to the current task. Filtering by tags can help narrow down to specific types of knowledge.")]
    public async Task<string> Search(
        [Description("The text to search for similar memories. Use natural language queries to find relevant reference or how-to information.")] string query,
        [Description("Maximum number of results to return")] int limit = 10,
        [Description("Minimum similarity threshold (0.0 to 1.0)")] double minSimilarity = 0.7,
        [Description("Optional tags to filter memories (e.g., 'reference', 'how-to', 'coding-standard')")] string[]? filterTags = null,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.Search");
        
        // Add query details as Activity event with structured data
        activity?.AddEvent(new ActivityEvent("query.details", DateTimeOffset.UtcNow, new ActivityTagsCollection
        {
            {"query.text", query},
            {"query.limit", limit.ToString()},
            {"query.minSimilarity", minSimilarity.ToString()},
            {"query.filterTags", filterTags != null ? string.Join(", ", filterTags) : "none"}
        }));

        // Search for similar memories
        List<Memory> memories = await _storage.Search(
            query,
            limit,
            minSimilarity,
            filterTags,
            cancellationToken
        );

        // Log results count
        _logger.LogInformation("Memory search completed. Query: {Query}, ResultCount: {ResultCount}, Threshold: {Threshold}", query, memories.Count, minSimilarity);

        bool usedFallback = false;
        double actualThreshold = minSimilarity;

        // If no results found, try with a 10% lower threshold (but not below 0.0)
        if (memories.Count == 0 && minSimilarity > 0.0)
        {
            double fallbackThreshold = Math.Max(0.0, minSimilarity - 0.1);
            
            _logger.LogInformation("No results found at threshold {OriginalThreshold}, trying fallback search at {FallbackThreshold}", minSimilarity, fallbackThreshold);
            
            activity?.AddEvent(new ActivityEvent("fallback.search", DateTimeOffset.UtcNow, new ActivityTagsCollection
            {
                {"fallback.threshold", fallbackThreshold.ToString()},
                {"original.threshold", minSimilarity.ToString()}
            }));

            memories = await _storage.Search(
                query,
                limit,
                fallbackThreshold,
                filterTags,
                cancellationToken
            );

            if (memories.Count > 0)
            {
                usedFallback = true;
                actualThreshold = fallbackThreshold;
                _logger.LogInformation("Fallback search found {ResultCount} results at threshold {FallbackThreshold}", memories.Count, fallbackThreshold);
            }
        }

        if (memories.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok, "No results found even with fallback");
            return "No memories found matching your query, even with a relaxed similarity threshold. Try using different search terms or lowering the similarity threshold further.";
        }

        // Log detailed results for each memory
        foreach (var memory in memories)
        {
            var relevancyScore = memory.Similarity.HasValue ? (100 * (1 - memory.Similarity.Value)) : 0;
            var relationshipCount = memory.Relationships?.Count ?? 0;
            
            _logger.LogInformation("Search result: MemoryId: {MemoryId}, Title: {Title}, RelevancyScore: {RelevancyScore:F1}%, RelationshipCount: {RelationshipCount}",
                memory.Id, memory.Title ?? "Untitled", relevancyScore, relationshipCount);
            
            // Log relationships if they exist
            if (memory.Relationships is { Count: > 0 })
            {
                foreach (var rel in memory.Relationships)
                {
                    _logger.LogInformation("Memory relationship: MemoryId: {MemoryId}, RelationshipType: {RelationType}, FromId: {FromId}, ToId: {ToId}",
                        memory.Id, rel.Type, rel.FromMemoryId, rel.ToMemoryId);
                }
            }
        }

        // Format the results
        StringBuilder result = new();
        
        if (usedFallback)
        {
            result.AppendLine($"No results found at similarity threshold {minSimilarity:F1}, but found {memories.Count} memories at relaxed threshold {actualThreshold:F1}:");
        }
        else
        {
            result.AppendLine($"Found {memories.Count} memories:");
        }
        result.AppendLine();

        // Collect all related memory IDs for suggestion
        var relatedMemoryIds = new HashSet<Guid>();

        foreach (var memory in memories)
        {
            result.AppendLine($"ID: {memory.Id}");
            if (memory.Title != null)
            {
                result.AppendLine($"Title: {memory.Title}");
            }
            result.AppendLine($"Type: {memory.Type}");
            result.AppendLine($"Text: {memory.Text}");
            result.AppendLine($"Source: {memory.Source}");
            result.AppendLine(
                $"Tags: {(memory.Tags != null ? string.Join(", ", memory.Tags) : "none")}"
            );
            result.AppendLine($"Confidence: {memory.Confidence:F2}");
            if (memory.Similarity.HasValue)
            {
                double percent = 100 * (1 - memory.Similarity.Value);
                result.AppendLine($"Similarity: {percent:F1}%");
            }
            // List relationships and collect related IDs
            if (memory.Relationships is { Count: > 0 })
            {
                result.AppendLine($"🔗 Relationships ({memory.Relationships.Count}):");
                foreach (var rel in memory.Relationships)
                {
                    var relatedId = rel.FromMemoryId == memory.Id ? rel.ToMemoryId : rel.FromMemoryId;
                    var direction = rel.FromMemoryId == memory.Id ? "→" : "←";
                    var relatedTitle = rel.RelatedMemoryTitle ?? "Untitled";
                    var relatedType = rel.RelatedMemoryType ?? "unknown";
                    
                    result.AppendLine($"  • [{rel.Type.ToUpper()}] {direction} \"{relatedTitle}\" ({relatedType}) [ID: {relatedId}]");
                    
                    // Collect related memory IDs (excluding the current memory)
                    if (rel.FromMemoryId != memory.Id)
                        relatedMemoryIds.Add(rel.FromMemoryId);
                    if (rel.ToMemoryId != memory.Id)
                        relatedMemoryIds.Add(rel.ToMemoryId);
                }
            }
            result.AppendLine($"Created: {memory.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            result.AppendLine();
        }

        // Add suggestion to load related memories if any exist
        if (relatedMemoryIds.Count > 0)
        {
            result.AppendLine("💡 Suggestion: These memories have relationships to other memories in the database.");
            result.AppendLine($"Consider using GetMany with these IDs to load related context: [{string.Join(", ", relatedMemoryIds)}]");
            result.AppendLine("This can provide additional relevant information and context for your task.");
        }

        activity?.SetStatus(ActivityStatusCode.Ok, $"Found {memories.Count} results" + (usedFallback ? " (with fallback)" : ""));
        return result.ToString();
    }

    [McpServerTool, Description("Retrieve a specific memory by ID. Use this to fetch a particular reference, how-to, or example by its unique identifier.")]
    public async Task<string> Get(
        [Description("The ID of the memory to retrieve. Use this to fetch a specific piece of reference or how-to information.")] Guid id,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.Get");
        
        // Add query details as Activity event
        activity?.AddEvent(new ActivityEvent("query.details", DateTimeOffset.UtcNow, new ActivityTagsCollection
        {
            {"query.id", id.ToString()}
        }));

        Memory? memory = await _storage.Get(id, cancellationToken);

        if (memory == null)
        {
            _logger.LogInformation("Memory not found for ID: {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Ok, "Memory not found");
            return $"Memory with ID {id} not found.";
        }

        // Log result details
        var relationshipCount = memory.Relationships?.Count ?? 0;
        _logger.LogInformation("Memory retrieved: MemoryId: {MemoryId}, Title: {Title}, Type: {Type}, RelationshipCount: {RelationshipCount}",
            memory.Id, memory.Title ?? "Untitled", memory.Type, relationshipCount);

        // Log relationships if they exist
        if (memory.Relationships is { Count: > 0 })
        {
            foreach (var rel in memory.Relationships)
            {
                _logger.LogInformation("Memory relationship: MemoryId: {MemoryId}, RelationshipType: {RelationType}, FromId: {FromId}, ToId: {ToId}",
                    memory.Id, rel.Type, rel.FromMemoryId, rel.ToMemoryId);
            }
        }

        StringBuilder result = new();
        result.AppendLine($"ID: {memory.Id}");
        if (memory.Title != null)
        {
            result.AppendLine($"Title: {memory.Title}");
        }
        result.AppendLine($"Type: {memory.Type}");
        result.AppendLine($"Text: {memory.Text}");
        result.AppendLine($"Source: {memory.Source}");
        result.AppendLine(
            $"Tags: {(memory.Tags != null ? string.Join(", ", memory.Tags) : "none")}"
        );
        result.AppendLine($"Confidence: {memory.Confidence:F2}");
        if (memory.Similarity.HasValue)
        {
            double percent = 100 * (1 - memory.Similarity.Value);
            result.AppendLine($"Similarity: {percent:F1}%");
        }
        
        // Collect related memory IDs for suggestion
        var relatedMemoryIds = new HashSet<Guid>();
        
        // List relationships
        if (memory.Relationships != null && memory.Relationships.Count > 0)
        {
            result.AppendLine($"🔗 Relationships ({memory.Relationships.Count}):");
            foreach (var rel in memory.Relationships)
            {
                var relatedId = rel.FromMemoryId == memory.Id ? rel.ToMemoryId : rel.FromMemoryId;
                var direction = rel.FromMemoryId == memory.Id ? "→" : "←";
                var relatedTitle = rel.RelatedMemoryTitle ?? "Untitled";
                var relatedType = rel.RelatedMemoryType ?? "unknown";
                
                result.AppendLine($"  • [{rel.Type.ToUpper()}] {direction} \"{relatedTitle}\" ({relatedType}) [ID: {relatedId}]");
                
                // Collect related memory IDs (excluding the current memory)
                if (rel.FromMemoryId != memory.Id)
                    relatedMemoryIds.Add(rel.FromMemoryId);
                if (rel.ToMemoryId != memory.Id)
                    relatedMemoryIds.Add(rel.ToMemoryId);
            }
        }
        result.AppendLine($"Created: {memory.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        result.AppendLine($"Updated: {memory.UpdatedAt:yyyy-MM-dd HH:mm:ss}");

        // Add suggestion to load related memories if any exist
        if (relatedMemoryIds.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("💡 Suggestion: This memory has relationships to other memories in the database.");
            result.AppendLine($"Consider using GetMany with these IDs to load related context: [{string.Join(", ", relatedMemoryIds)}]");
            result.AppendLine("This can provide additional relevant information and context for your task.");
        }

        activity?.SetStatus(ActivityStatusCode.Ok, "Memory retrieved successfully");
        return result.ToString();
    }

    [McpServerTool, Description("Delete a memory by ID. Use this to remove outdated or incorrect reference or how-to information.")]
    public async Task<string> Delete(
        [Description("The ID of the memory to delete. Use this to remove a specific piece of knowledge.")] Guid id,
        CancellationToken cancellationToken = default
    )
    {
        bool success = await _storage.Delete(id, cancellationToken);

        return success ? $"Memory with ID {id} deleted successfully." : $"Memory with ID {id} not found or could not be deleted.";
    }

    [McpServerTool, Description("Fetch multiple memories by their IDs. Use this to retrieve a set of related reference materials, how-tos, or examples.")]
    public async Task<string> GetMany(
        [Description("The list of memory IDs to fetch. Use this to retrieve multiple related pieces of knowledge at once.")] Guid[] ids,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.GetMany");
        
        // Add query details as Activity event
        activity?.AddEvent(new ActivityEvent("query.details", DateTimeOffset.UtcNow, new ActivityTagsCollection
        {
            {"query.ids", string.Join(", ", ids)},
            {"query.count", ids.Length.ToString()}
        }));

        var memories = await _storage.GetMany(ids, cancellationToken);
        
        // Log results count
        _logger.LogInformation("GetMany completed. RequestedCount: {RequestedCount}, FoundCount: {FoundCount}", ids.Length, memories.Count);
        
        if (memories.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok, "No memories found");
            return "No memories found for the provided IDs.";
        }
        
        // Log details for each retrieved memory
        foreach (var memory in memories)
        {
            _logger.LogInformation("Memory retrieved: MemoryId: {MemoryId}, Title: {Title}, Type: {Type}",
                memory.Id, memory.Title ?? "Untitled", memory.Type);
        }

        StringBuilder result = new();
        result.AppendLine($"Found {memories.Count} memories:");
        result.AppendLine();
        
        // Collect all related memory IDs for suggestion
        var relatedMemoryIds = new HashSet<Guid>();
        
        foreach (var memory in memories)
        {
            result.AppendLine($"ID: {memory.Id}");
            if (memory.Title != null)
            {
                result.AppendLine($"Title: {memory.Title}");
            }
            result.AppendLine($"Type: {memory.Type}");
            result.AppendLine($"Text: {memory.Text}");
            result.AppendLine($"Source: {memory.Source}");
            result.AppendLine($"Tags: {(memory.Tags != null ? string.Join(", ", memory.Tags) : "none")}");
            result.AppendLine($"Confidence: {memory.Confidence:F2}");
            
            // List relationships and collect related IDs
            if (memory.Relationships is { Count: > 0 })
            {
                result.AppendLine($"🔗 Relationships ({memory.Relationships.Count}):");
                foreach (var rel in memory.Relationships)
                {
                    var relatedId = rel.FromMemoryId == memory.Id ? rel.ToMemoryId : rel.FromMemoryId;
                    var direction = rel.FromMemoryId == memory.Id ? "→" : "←";
                    var relatedTitle = rel.RelatedMemoryTitle ?? "Untitled";
                    var relatedType = rel.RelatedMemoryType ?? "unknown";
                    
                    result.AppendLine($"  • [{rel.Type.ToUpper()}] {direction} \"{relatedTitle}\" ({relatedType}) [ID: {relatedId}]");
                    
                    // Collect related memory IDs (excluding memories we already have)
                    if (rel.FromMemoryId != memory.Id && !ids.Contains(rel.FromMemoryId))
                        relatedMemoryIds.Add(rel.FromMemoryId);
                    if (rel.ToMemoryId != memory.Id && !ids.Contains(rel.ToMemoryId))
                        relatedMemoryIds.Add(rel.ToMemoryId);
                }
            }
            
            result.AppendLine($"Created: {memory.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            result.AppendLine();
        }
        
        // Add suggestion to load related memories if any exist
        if (relatedMemoryIds.Count > 0)
        {
            result.AppendLine("💡 Suggestion: These memories have relationships to other memories not included in this result.");
            result.AppendLine($"Consider using GetMany with these additional IDs to load more related context: [{string.Join(", ", relatedMemoryIds)}]");
            result.AppendLine("This can provide additional relevant information and context for your task.");
        }
        
        activity?.SetStatus(ActivityStatusCode.Ok, $"Retrieved {memories.Count} memories");
        return result.ToString();
    }

    [McpServerTool, Description("Create a relationship between two memories. Use this to link related reference materials, how-tos, or examples (e.g., 'example-of', 'explains', 'related-to'). Relationships help organize knowledge for easier retrieval and understanding.")]
    public async Task<string> CreateRelationship(
        [Description("The ID of the source memory (e.g., the reference or how-to that is providing context)")] Guid fromId,
        [Description("The ID of the target memory (e.g., the example or related reference)")] Guid toId,
        [Description("The type of relationship (e.g., 'example-of', 'explains', 'related-to'). Use relationships to connect and organize knowledge.")] string type,
        CancellationToken cancellationToken = default
    )
    {
        var rel = await _storage.CreateRelationship(fromId, toId, type, cancellationToken);
        return $"Relationship created: {rel.Id} from {rel.FromMemoryId} to {rel.ToMemoryId} (type: {rel.Type})";
    }
}
