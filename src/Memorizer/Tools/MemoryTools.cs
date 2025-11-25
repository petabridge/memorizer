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

    [McpServerTool, Description("Store a new memory or update an existing one. For new memories: creates a new entry with provided data. For updates (when 'id' is provided): updates an existing memory with options to replace, append, or prepend content. All changes are versioned. Use this to save reference material, how-to guides, coding standards, or any information you (the LLM) may want to refer to when completing tasks.")]
    public async Task<string> Store(
        [Description("The type of memory (e.g., 'conversation', 'document', 'reference', 'how-to', etc.). Use 'reference' or 'how-to' for reusable knowledge.")] string type,
        [Description("Plain text (markdown, code, prose, etc.) to store or update.")] string text,
        [Description("The source of the memory (e.g., 'user', 'system', 'LLM', etc.). Use 'LLM' if you are storing knowledge for your own future use.")] string source,
        [Description("Title for the memory. Required for new memories.")] string title,
        [Description("Optional tags to categorize the memory. Use tags like 'coding-standard', 'unit-test', 'reference', 'how-to', etc. to make retrieval easier.")] string[]? tags = null,
        [Description("Confidence score for the memory (0.0 to 1.0)")] double confidence = 1.0,
        [Description("Optionally, the ID of a related memory. Use this to link related reference materials, how-tos, or examples.")] Guid? relatedTo = null,
        [Description("Optionally, the type of relationship to create (e.g., 'example-of', 'explains', 'related-to'). Use relationships to connect related knowledge.")] string? relationshipType = null,
        [Description("Optional: ID of an existing memory to update. If provided, updates the existing memory instead of creating a new one. Creates a new version.")] Guid? id = null,
        [Description("Update mode when 'id' is provided: 'replace' (default) replaces all content, 'append' adds to end, 'prepend' adds to beginning, 'section' replaces content between section markers.")] string updateMode = "replace",
        [Description("For 'section' mode: the marker to identify the section to replace (e.g., '## Notes' or '<!-- daily-log -->'). Content between this marker and the next marker of same type (or end of text) will be replaced.")] string? sectionMarker = null,
        [Description("For 'append'/'prepend' modes: separator between existing and new content. Default is '\\n\\n'.")] string? appendSeparator = null,
        CancellationToken cancellationToken = default
    )
    {
        Memory memory;
        bool isUpdate = id.HasValue;

        if (isUpdate && id.HasValue)
        {
            // Update existing memory
            var existingMemory = await _storage.Get(id.Value, cancellationToken);
            if (existingMemory == null)
            {
                return $"Memory with ID {id.Value} not found. Cannot update non-existent memory.";
            }

            // Apply update mode
            string finalText = ApplyUpdateMode(existingMemory.Text, text, updateMode, sectionMarker, appendSeparator);

            var updatedMemory = await _storage.UpdateMemory(
                id.Value,
                type,
                finalText,
                source,
                tags,
                confidence,
                title,
                cancellationToken
            );

            if (updatedMemory == null)
            {
                return $"Failed to update memory with ID {id.Value}.";
            }

            memory = updatedMemory;
        }
        else
        {
            // Create new memory
            memory = await _storage.StoreMemory(
                type,
                text,
                source,
                tags,
                confidence,
                title: title,
                cancellationToken: cancellationToken
            );
        }

        // Handle manual relationship creation if specified
        if (relatedTo.HasValue && !string.IsNullOrWhiteSpace(relationshipType))
        {
            await _storage.CreateRelationship(memory.Id, relatedTo.Value, relationshipType, cancellationToken);
        }

        if (isUpdate)
        {
            return $"Memory updated successfully. ID: {memory.Id}, Version: {memory.CurrentVersion}. Changes have been versioned and can be viewed/reverted via version history.";
        }

        return $"Memory stored successfully with ID: {memory.Id}. You might want to call `CreateRelationship` to associate this memory with another memory for better context retrieval.";
    }

    private static string ApplyUpdateMode(string existingText, string newText, string updateMode, string? sectionMarker, string? appendSeparator)
    {
        var separator = appendSeparator ?? "\n\n";

        return updateMode.ToLowerInvariant() switch
        {
            "append" => existingText + separator + newText,
            "prepend" => newText + separator + existingText,
            "section" when !string.IsNullOrWhiteSpace(sectionMarker) => ReplaceSectionContent(existingText, newText, sectionMarker),
            _ => newText // "replace" or default
        };
    }

    private static string ReplaceSectionContent(string existingText, string newContent, string sectionMarker)
    {
        var lines = existingText.Split('\n');
        var result = new StringBuilder();
        var inSection = false;
        var sectionFound = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith(sectionMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (!inSection)
                {
                    // Found the start of our section
                    inSection = true;
                    sectionFound = true;
                    result.AppendLine(line);
                    result.AppendLine(newContent);
                }
                else
                {
                    // Found the next section with same marker, exit our section
                    inSection = false;
                    result.AppendLine(line);
                }
            }
            else if (!inSection)
            {
                result.AppendLine(line);
            }
            // Skip lines within the section being replaced
        }

        // If section not found, append it at the end
        if (!sectionFound)
        {
            result.AppendLine();
            result.AppendLine(sectionMarker);
            result.AppendLine(newContent);
        }

        return result.ToString().TrimEnd('\n', '\r');
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

    [McpServerTool, Description("Retrieve a specific memory by ID, optionally with version history. Use this to fetch a particular reference, how-to, or example. Supports retrieving a specific version or including the version history timeline.")]
    public async Task<string> Get(
        [Description("The ID of the memory to retrieve. Use this to fetch a specific piece of reference or how-to information.")] Guid id,
        [Description("Optional: If true, includes version history summary in the response (recent versions, change count).")] bool includeVersionHistory = false,
        [Description("Optional: Specific version number to retrieve. If provided, returns that version's content instead of current.")] int? versionNumber = null,
        [Description("Optional: Maximum number of versions to include in history (default: 5, max: 20).")] int versionLimit = 5,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.Get");

        // Add query details as Activity event
        activity?.AddEvent(new ActivityEvent("query.details", DateTimeOffset.UtcNow, new ActivityTagsCollection
        {
            {"query.id", id.ToString()},
            {"query.includeVersionHistory", includeVersionHistory.ToString()},
            {"query.versionNumber", versionNumber?.ToString() ?? "current"}
        }));

        // If requesting a specific version, get that version
        if (versionNumber.HasValue)
        {
            var version = await _storage.GetVersion(id, versionNumber.Value, cancellationToken);
            if (version == null)
            {
                return $"Version {versionNumber.Value} not found for memory ID {id}.";
            }

            StringBuilder versionResult = new();
            versionResult.AppendLine($"📜 VERSION {version.VersionNumber} (Historical Snapshot)");
            versionResult.AppendLine($"ID: {version.MemoryId}");
            versionResult.AppendLine($"Title: {version.Title ?? "Untitled"}");
            versionResult.AppendLine($"Type: {version.Type}");
            versionResult.AppendLine($"Text: {version.Text}");
            versionResult.AppendLine($"Source: {version.Source}");
            versionResult.AppendLine($"Tags: {(version.Tags != null ? string.Join(", ", version.Tags) : "none")}");
            versionResult.AppendLine($"Confidence: {version.Confidence:F2}");
            versionResult.AppendLine($"Original Created: {version.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            versionResult.AppendLine($"Version Created: {version.VersionedAt:yyyy-MM-dd HH:mm:ss}");

            if (version.Events is { Count: > 0 })
            {
                versionResult.AppendLine();
                versionResult.AppendLine("📝 Changes in this version:");
                foreach (var evt in version.Events)
                {
                    versionResult.AppendLine($"  • {evt.GetDisplayText()}");
                }
            }

            versionResult.AppendLine();
            versionResult.AppendLine("💡 Use RevertToVersion to restore this version as current, or Get with versionNumber to view other versions.");

            activity?.SetStatus(ActivityStatusCode.Ok, "Version retrieved successfully");
            return versionResult.ToString();
        }

        // Get current memory
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
        result.AppendLine($"Current Version: {memory.CurrentVersion}");
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

        // Include version history if requested
        if (includeVersionHistory)
        {
            var limitClamped = Math.Clamp(versionLimit, 1, 20);
            var versions = await _storage.GetVersionHistory(id, limitClamped, cancellationToken);

            if (versions.Count > 0)
            {
                result.AppendLine();
                result.AppendLine($"📜 Version History (showing {versions.Count} most recent):");
                foreach (var version in versions)
                {
                    var changeTypes = version.Events?.Select(e => e.EventType).Distinct().ToList() ?? [];
                    var changesDesc = changeTypes.Count > 0 ? string.Join(", ", changeTypes) : "initial";
                    result.AppendLine($"  v{version.VersionNumber} ({version.VersionedAt:yyyy-MM-dd HH:mm}) - {changesDesc}");
                }
                result.AppendLine();
                result.AppendLine("💡 Use Get with versionNumber parameter to view a specific version, or RevertToVersion to restore.");
            }
        }

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

    [McpServerTool, Description("Revert a memory to a previous version. This restores all content, metadata (title, tags, confidence), and the type from the specified version. Creates a new version recording the revert operation. Embeddings are regenerated. Use Get with includeVersionHistory=true first to see available versions.")]
    public async Task<string> RevertToVersion(
        [Description("The ID of the memory to revert.")] Guid id,
        [Description("The version number to revert to. Use Get with includeVersionHistory=true to see available versions.")] int versionNumber,
        [Description("Optional: Identifier of who is requesting the revert (e.g., 'user', 'LLM', 'system').")] string? changedBy = null,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.RevertToVersion");

        activity?.AddEvent(new ActivityEvent("revert.details", DateTimeOffset.UtcNow, new ActivityTagsCollection
        {
            {"memory.id", id.ToString()},
            {"target.version", versionNumber.ToString()},
            {"changed.by", changedBy ?? "unspecified"}
        }));

        // First check the memory exists
        var existingMemory = await _storage.Get(id, cancellationToken);
        if (existingMemory == null)
        {
            _logger.LogInformation("Cannot revert: Memory not found for ID: {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Ok, "Memory not found");
            return $"Memory with ID {id} not found. Cannot revert.";
        }

        // Check if already at the target version
        if (existingMemory.CurrentVersion == versionNumber)
        {
            return $"Memory is already at version {versionNumber}. No changes made.";
        }

        // Perform the revert
        var revertedMemory = await _storage.RevertToVersion(id, versionNumber, changedBy, cancellationToken);

        if (revertedMemory == null)
        {
            _logger.LogWarning("Failed to revert memory {MemoryId} to version {Version}", id, versionNumber);
            activity?.SetStatus(ActivityStatusCode.Error, "Revert failed");
            return $"Failed to revert memory to version {versionNumber}. The version may not exist. Use Get with includeVersionHistory=true to see available versions.";
        }

        _logger.LogInformation("Memory {MemoryId} reverted from version {OldVersion} to {TargetVersion}, now at version {NewVersion}",
            id, existingMemory.CurrentVersion, versionNumber, revertedMemory.CurrentVersion);

        activity?.SetStatus(ActivityStatusCode.Ok, "Memory reverted successfully");

        StringBuilder result = new();
        result.AppendLine($"✅ Memory successfully reverted to version {versionNumber}.");
        result.AppendLine();
        result.AppendLine("Restored state:");
        result.AppendLine($"  ID: {revertedMemory.Id}");
        result.AppendLine($"  Title: {revertedMemory.Title ?? "Untitled"}");
        result.AppendLine($"  Type: {revertedMemory.Type}");
        result.AppendLine($"  Tags: {(revertedMemory.Tags != null ? string.Join(", ", revertedMemory.Tags) : "none")}");
        result.AppendLine($"  Confidence: {revertedMemory.Confidence:F2}");
        result.AppendLine($"  New Version: {revertedMemory.CurrentVersion}");
        result.AppendLine();
        result.AppendLine("Note: A new version has been created recording this revert. Embeddings have been regenerated.");
        result.AppendLine("Use Get with id to see the full restored content.");

        return result.ToString();
    }
}
