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
        [Description("The type of memory (e.g., 'conversation', 'document', 'reference', 'how-to', 'todo-list', etc.). Use 'reference' or 'how-to' for reusable knowledge.")] string type,
        [Description("Plain text (markdown, code, prose, etc.) to store. Include as much context as possible.")] string text,
        [Description("The source of the memory (e.g., 'user', 'system', 'LLM', etc.). Use 'LLM' if you are storing knowledge for your own future use.")] string source,
        [Description("Title for the memory. Should be descriptive and searchable.")] string title,
        [Description("Optional tags to categorize the memory. Use tags like 'coding-standard', 'unit-test', 'reference', 'how-to', 'todo', etc. to make retrieval easier.")] string[]? tags = null,
        [Description("Confidence score for the memory (0.0 to 1.0)")] double confidence = 1.0,
        [Description("Optionally, the ID of a related memory. Use this to link related reference materials, how-tos, or examples.")] Guid? relatedTo = null,
        [Description("Optionally, the type of relationship to create (e.g., 'example-of', 'explains', 'related-to'). Use relationships to connect related knowledge.")] string? relationshipType = null,
        CancellationToken cancellationToken = default
    )
    {
        // Create new memory
        var memory = await _storage.StoreMemory(
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

        return $"Memory stored successfully with ID: {memory.Id}. Use Edit tool to make targeted updates, or CreateRelationship to link to other memories.";
    }

    [McpServerTool, Description("Edit an existing memory using find-and-replace. Ideal for checking off to-do items, updating sections, or fixing typos. IMPORTANT: The edit will FAIL if old_text is not found exactly - always use Get first to see current content and copy the exact text to replace. All changes are versioned and can be reverted.")]
    public async Task<string> Edit(
        [Description("The ID of the memory to edit.")] Guid id,
        [Description("The exact text to find and replace. Must match exactly (case-sensitive). For multi-line replacements, include the full text including newlines.")] string old_text,
        [Description("The text to replace it with. Can be different length than old_text.")] string new_text,
        [Description("If true, replaces ALL occurrences of old_text. If false (default), only replaces the first occurrence. Use false for safety when editing unique content.")] bool replace_all = false,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.Edit");

        activity?.AddEvent(new ActivityEvent("edit.details", DateTimeOffset.UtcNow, new ActivityTagsCollection
        {
            {"memory.id", id.ToString()},
            {"old_text.length", old_text.Length.ToString()},
            {"new_text.length", new_text.Length.ToString()},
            {"replace_all", replace_all.ToString()}
        }));

        // Get existing memory
        var existingMemory = await _storage.Get(id, cancellationToken);
        if (existingMemory == null)
        {
            _logger.LogInformation("Edit failed: Memory not found for ID: {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Ok, "Memory not found");
            return $"Memory with ID {id} not found. Cannot edit non-existent memory.";
        }

        // Check if old_text exists in the content
        if (!existingMemory.Text.Contains(old_text))
        {
            _logger.LogInformation("Edit failed: old_text not found in memory {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Ok, "old_text not found");

            // Provide helpful error message
            var preview = existingMemory.Text.Length > 200
                ? existingMemory.Text.Substring(0, 200) + "..."
                : existingMemory.Text;
            return $"Edit failed: The specified old_text was not found in the memory content.\n\n" +
                   $"old_text you provided ({old_text.Length} chars):\n\"{old_text}\"\n\n" +
                   $"Current memory content preview:\n\"{preview}\"\n\n" +
                   "Tip: Use Get tool first to see the exact current content, then copy the exact text you want to replace.";
        }

        // Perform the replacement
        string newContent;
        int replacementCount;

        if (replace_all)
        {
            replacementCount = CountOccurrences(existingMemory.Text, old_text);
            newContent = existingMemory.Text.Replace(old_text, new_text);
        }
        else
        {
            // Replace only first occurrence
            var index = existingMemory.Text.IndexOf(old_text, StringComparison.Ordinal);
            newContent = existingMemory.Text.Substring(0, index) + new_text + existingMemory.Text.Substring(index + old_text.Length);
            replacementCount = 1;
        }

        // Check if anything actually changed
        if (newContent == existingMemory.Text)
        {
            return "No changes made - the replacement would result in identical content.";
        }

        // Update the memory with new content (keeps all other metadata the same)
        var updatedMemory = await _storage.UpdateMemory(
            id,
            existingMemory.Type,
            newContent,
            existingMemory.Source,
            existingMemory.Tags,
            existingMemory.Confidence,
            existingMemory.Title,
            cancellationToken
        );

        if (updatedMemory == null)
        {
            _logger.LogWarning("Edit failed: Could not update memory {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Error, "Update failed");
            return $"Failed to save edit to memory {id}.";
        }

        _logger.LogInformation("Memory {MemoryId} edited successfully. Replacements: {Count}, New version: {Version}",
            id, replacementCount, updatedMemory.CurrentVersion);
        activity?.SetStatus(ActivityStatusCode.Ok, "Edit successful");

        return $"Edit successful. Made {replacementCount} replacement(s). Memory ID: {id}, New Version: {updatedMemory.CurrentVersion}.\n" +
               "Changes are versioned and can be reverted using RevertToVersion if needed.";
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    [McpServerTool, Description("Update a memory's metadata (title, type, tags, confidence) without changing the content or regenerating embeddings. Use Edit tool for content changes. All changes are versioned and can be reverted.")]
    public async Task<string> UpdateMetadata(
        [Description("The ID of the memory to update.")] Guid id,
        [Description("Optional: New title for the memory. Pass null to keep existing.")] string? title = null,
        [Description("Optional: New type for the memory. Pass null to keep existing.")] string? type = null,
        [Description("Optional: New tags for the memory. Pass null to keep existing, pass empty array to clear tags.")] string[]? tags = null,
        [Description("Optional: New confidence score (0.0 to 1.0). Pass null to keep existing.")] double? confidence = null,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.UpdateMetadata");

        // Get existing memory
        var existingMemory = await _storage.Get(id, cancellationToken);
        if (existingMemory == null)
        {
            _logger.LogInformation("UpdateMetadata failed: Memory not found for ID: {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Ok, "Memory not found");
            return $"Memory with ID {id} not found.";
        }

        // Use existing values for any null parameters
        var newTitle = title ?? existingMemory.Title;
        var newType = type ?? existingMemory.Type;
        var newTags = tags ?? existingMemory.Tags;
        var newConfidence = confidence ?? existingMemory.Confidence;

        // Update the memory (content stays the same)
        var updatedMemory = await _storage.UpdateMemory(
            id,
            newType,
            existingMemory.Text,
            existingMemory.Source,
            newTags,
            newConfidence,
            newTitle,
            cancellationToken
        );

        if (updatedMemory == null)
        {
            _logger.LogWarning("UpdateMetadata failed: Could not update memory {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Error, "Update failed");
            return $"Failed to update metadata for memory {id}.";
        }

        _logger.LogInformation("Memory {MemoryId} metadata updated. New version: {Version}", id, updatedMemory.CurrentVersion);
        activity?.SetStatus(ActivityStatusCode.Ok, "Metadata update successful");

        var changes = new List<string>();
        if (title != null) changes.Add($"title='{title}'");
        if (type != null) changes.Add($"type='{type}'");
        if (tags != null) changes.Add($"tags=[{string.Join(", ", tags)}]");
        if (confidence != null) changes.Add($"confidence={confidence:F2}");

        return $"Metadata updated successfully. Changes: {string.Join(", ", changes)}. Memory ID: {id}, New Version: {updatedMemory.CurrentVersion}.";
    }

    [McpServerTool, Description("Search for memories similar to the provided text. Use this to retrieve reference material, how-tos, or examples relevant to the current task. Filtering by tags can help narrow down to specific types of knowledge.")]
    public async Task<string> SearchMemories(
        [Description("The text to search for similar memories. Use natural language queries to find relevant reference or how-to information.")] string query,
        [Description("Maximum number of results to return")] int limit = 10,
        [Description("Minimum similarity threshold (0.0 to 1.0)")] double minSimilarity = 0.7,
        [Description("Optional tags to filter memories (e.g., 'reference', 'how-to', 'coding-standard')")] string[]? filterTags = null,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.SearchMemories");
        
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

    [McpServerTool, Description("Retrieve a specific memory by ID. Use this to fetch a particular reference, how-to, or example by its unique identifier. Optionally include version history or retrieve a specific past version.")]
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
                    versionResult.AppendLine($"  • {evt.DisplayText}");
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

    [McpServerTool, Description("Delete a memory by ID. This permanently removes the memory including all version history. Use this to remove outdated or incorrect reference or how-to information.")]
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

    [McpServerTool, Description("Revert a memory to a previous version. Restores all content and metadata (title, type, tags, confidence) from the specified version. Creates a new version recording the revert operation and regenerates embeddings. Use Get with includeVersionHistory=true to see available versions first.")]
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
