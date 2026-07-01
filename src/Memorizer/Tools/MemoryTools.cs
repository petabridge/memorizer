using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Memory = Memorizer.Models.Memory;
using System.Linq;
using System.Diagnostics;
using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Memorizer.Telemetry;
using Microsoft.Extensions.Logging;

namespace PostgMem.Tools;

[McpServerToolType]
public class MemoryTools
{
    private readonly IStorage _storage;
    private readonly ILogger<MemoryTools> _logger;
    private readonly SearchSettings _searchSettings;
    private readonly ICanonicalUrlService _canonicalUrlService;

    public MemoryTools(IStorage storage, ILogger<MemoryTools> logger, SearchSettings searchSettings, ICanonicalUrlService canonicalUrlService)
    {
        _storage = storage;
        _logger = logger;
        _searchSettings = searchSettings;
        _canonicalUrlService = canonicalUrlService;
    }

    [McpServerTool, Description("Store a new memory in the database, optionally creating a relationship to another memory. Use this to save reference material, how-to guides, coding standards, or any information you (the LLM) may want to refer to when completing tasks. Include as much context as possible, such as markdown, code samples, and detailed explanations. Create relationships to link related reference materials or examples.")]
    public async Task<string> Store(
        [Description("The type of memory (e.g., 'conversation', 'document', 'reference', 'how-to', 'todo-list', etc.). Use 'reference' or 'how-to' for reusable knowledge.")] string type,
        [Description("Plain text (markdown, code, prose, etc.) to store. Include as much context as possible.")] string text,
        [Description("The source of the memory (e.g., 'user', 'system', 'LLM', etc.). Use 'LLM' if you are storing knowledge for your own future use.")] string source,
        [Description("Title for the memory. Should be descriptive and searchable.")] string title,
        [Description("Optional tags to categorize the memory. Use tags like 'coding-standard', 'unit-test', 'reference', 'how-to', 'todo', etc. to make retrieval easier.")] string[]? tags = null,
        [Description("Confidence score for the memory (0.0 to 1.0)")] double confidence = 1.0,
        [Description("Optionally, the ID of a related memory. Use this to link related reference materials, how-tos, or examples.")] string? relatedTo = null,
        [Description("Optionally, the type of relationship to create (e.g., 'example-of', 'explains', 'related-to'). Use relationships to connect related knowledge.")] string? relationshipType = null,
        [Description("Optional project ID to assign this memory to. If not provided, memory is stored in the Unfiled workspace. Use ListProjects to find available projects.")] string? projectId = null,
        [Description("Memory archetype: 'document' for living, editable content (default) or 'record' for historical, immutable records like work logs.")] string archetype = "document",
        CancellationToken cancellationToken = default
    )
    {
        // Parse archetype string to enum
        var archetypeEnum = ArchetypeEnumExtensions.ParseArchetype(archetype);

        // Parse optional Guid parameters defensively — MCP clients may send empty strings or "null"
        var parsedProjectId = ParseOptionalGuid(projectId);
        var parsedRelatedTo = ParseOptionalGuid(relatedTo);

        // Determine owner based on projectId
        MemoryOwner? owner = parsedProjectId.HasValue
            ? MemoryOwner.ForProject(new ProjectId(parsedProjectId.Value))
            : null; // null defaults to Unfiled in storage layer

        // Create new memory
        var memory = await _storage.StoreMemory(
            type,
            text,
            source,
            tags,
            new Confidence(confidence),
            title: title,
            owner: owner,
            archetype: archetypeEnum,
            cancellationToken: cancellationToken
        );

        // Handle manual relationship creation if specified
        if (parsedRelatedTo.HasValue && !string.IsNullOrWhiteSpace(relationshipType))
        {
            await _storage.CreateRelationship(memory.Id, (MemoryId)parsedRelatedTo.Value, relationshipType, cancellationToken);
        }

        var locationInfo = owner != null
            ? $"Assigned to project {parsedProjectId}."
            : "Stored in Unfiled workspace.";

        var urlInfo = _canonicalUrlService.IsConfigured
            ? $"\n\nView in web UI: {_canonicalUrlService.GetMemoryUrl(memory.Id)}"
            : "";

        return $"Memory stored successfully with ID: {memory.Id}. {locationInfo} Archetype: {archetypeEnum.ToStringValue()}. Use Edit tool to make targeted updates, or CreateReference to link to other memories.{urlInfo}";
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

        var memoryId = (MemoryId)id;

        // Get existing memory
        var existingMemory = await _storage.Get(memoryId, cancellationToken);
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
            memoryId,
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

    /// <summary>
    /// Formats a MemoryOwner for display in tool responses.
    /// Shows "Unfiled" for the default workspace, otherwise shows type and ID.
    /// </summary>
    private static string FormatOwner(MemoryOwner owner)
    {
        if (owner.IsUnfiled)
        {
            return "Unfiled";
        }

        return owner.Type == OwnerTypeEnum.Project
            ? $"Project ({owner.Id})"
            : $"Workspace ({owner.Id})";
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

        var memoryId = (MemoryId)id;

        // Get existing memory
        var existingMemory = await _storage.Get(memoryId, cancellationToken);
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
        var newConfidence = confidence.HasValue ? new Confidence(confidence.Value) : existingMemory.Confidence;

        // Update the memory (content stays the same)
        var updatedMemory = await _storage.UpdateMemory(
            memoryId,
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
        [Description("Optional project ID to scope search to. If provided, only searches memories assigned to this project. Use ListProjects to find available projects. Mutually exclusive with workspaceId.")] string? projectId = null,
        [Description("When projectId is specified, also include memories in the Unfiled workspace. Useful for finding unorganized content that might be relevant.")] bool includeUnassigned = false,
        [Description("Include archived memories in search results. Default is false (archived memories are hidden).")] bool includeArchived = false,
        [Description("Optional workspace ID to scope search to. If provided, searches memories owned directly by the workspace plus all memories owned by projects within it (direct children only, not sub-workspaces). Mutually exclusive with projectId.")] string? workspaceId = null,
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
            {"query.filterTags", filterTags != null ? string.Join(", ", filterTags) : "none"},
            {"query.projectId", projectId ?? "none"},
            {"query.workspaceId", workspaceId ?? "none"},
            {"query.includeUnassigned", includeUnassigned.ToString()},
            {"query.includeArchived", includeArchived.ToString()}
        }));

        // Convert projectId / workspaceId to typed IDs — parse defensively since MCP clients may send empty strings
        if (!TryParseOptionalGuid(projectId, out var parsedProjectId))
        {
            return "projectId must be a valid GUID, empty, or null.";
        }

        if (!TryParseOptionalGuid(workspaceId, out var parsedWorkspaceId))
        {
            return "workspaceId must be a valid GUID, empty, or null.";
        }

        if (parsedProjectId.HasValue && parsedWorkspaceId.HasValue)
        {
            return "projectId and workspaceId are mutually exclusive search scopes. Provide only one.";
        }
        ProjectId? typedProjectId = parsedProjectId.HasValue ? new ProjectId(parsedProjectId.Value) : null;
        WorkspaceId? typedWorkspaceId = parsedWorkspaceId.HasValue ? new WorkspaceId(parsedWorkspaceId.Value) : null;

        // Use hybrid search combining vector similarity + PostgreSQL full-text search via RRF
        List<Memory> memories = await _storage.HybridSearch(
            query,
            limit,
            new SimilarityScore(minSimilarity),
            filterTags,
            typedProjectId,
            includeUnassigned,
            includeArchived,
            includeSystem: false,
            typedWorkspaceId,
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

            memories = await _storage.HybridSearch(
                query,
                limit,
                new SimilarityScore(fallbackThreshold),
                filterTags,
                typedProjectId,
                includeUnassigned,
                includeArchived,
                includeSystem: false,
                typedWorkspaceId,
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
            var relevancyScore = memory.Similarity.HasValue ? (100 * (double)memory.Similarity.Value) : 0;
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

        // Collect all memory IDs for retrieval suggestion
        var memoryIds = new List<Guid>();

        foreach (var memory in memories)
        {
            memoryIds.Add(memory.Id.Value);

            result.AppendLine($"ID: {memory.Id}");
            if (memory.Title != null)
            {
                result.AppendLine($"Title: {memory.Title}");
            }
            result.AppendLine($"Type: {memory.Type}");

            // Show owner (workspace/project) for organization context
            result.AppendLine($"Owner: {FormatOwner(memory.Owner)}");

            // Show archetype status (especially important when includeArchived is true)
            result.AppendLine($"Archetype: {memory.Archetype.ToStringValue()}{(memory.Archetype.IsArchived() ? " ⚠️" : "")}");

            // Only include full content if configured to do so
            if (_searchSettings.ReturnFullContent)
            {
                result.AppendLine($"Text: {memory.Text}");
                result.AppendLine($"Source: {memory.Source}");
            }

            result.AppendLine(
                $"Tags: {(memory.Tags != null ? string.Join(", ", memory.Tags) : "none")}"
            );

            if (_searchSettings.ReturnFullContent)
            {
                result.AppendLine($"Confidence: {memory.Confidence:F2}");
            }

            if (memory.Similarity.HasValue)
            {
                double percent = 100 * (double)memory.Similarity.Value;
                result.AppendLine($"Similarity: {percent:F1}%");
            }

            // Only show relationships if returning full content
            if (_searchSettings.ReturnFullContent && memory.Relationships is { Count: > 0 })
            {
                result.AppendLine($"Relationships: {memory.Relationships.Count}");
            }

            result.AppendLine($"Created: {memory.CreatedAt:yyyy-MM-dd HH:mm:ss}");

            // Add canonical URL if configured
            if (_canonicalUrlService.IsConfigured)
            {
                result.AppendLine($"URL: {_canonicalUrlService.GetMemoryUrl(memory.Id)}");
            }

            result.AppendLine();
        }

        // Add retrieval instructions for lightweight results
        if (!_searchSettings.ReturnFullContent)
        {
            result.AppendLine("---");
            result.AppendLine("To retrieve the full content of these memories, use one of the following:");
            result.AppendLine($"• Get tool with a specific memory ID to fetch one memory");
            result.AppendLine($"• GetMany tool with IDs: [{string.Join(", ", memoryIds)}]");
        }

        activity?.SetStatus(ActivityStatusCode.Ok, $"Found {memories.Count} results" + (usedFallback ? " (with fallback)" : ""));
        return result.ToString();
    }

    [McpServerTool, Description("Retrieve a specific memory by ID. Use this to fetch a particular reference, how-to, or example by its unique identifier. Optionally include version history or retrieve a specific past version. By default, also shows similar memories that may be candidates for consolidation or linking.")]
    public async Task<string> Get(
        [Description("The ID of the memory to retrieve. Use this to fetch a specific piece of reference or how-to information.")] Guid id,
        [Description("Optional: If true, includes version history summary in the response (recent versions, change count).")] bool includeVersionHistory = false,
        [Description("Optional: Specific version number to retrieve. If provided, returns that version's content instead of current.")] int? versionNumber = null,
        [Description("Optional: Maximum number of versions to include in history (default: 5, max: 20).")] int versionLimit = 5,
        [Description("Optional: If true, includes relationships pointing to archived memories. Default is false (archived targets are hidden).")] bool includeArchivedRelationships = false,
        [Description("Optional: If true (default), shows similar memories based on content embedding. Useful for discovering consolidation candidates. Set to false to skip similarity check.")] bool includeSimilar = true,
        [Description("Optional: Minimum similarity threshold for similar memories (0.0 to 1.0). Default is 0.75.")] double similarityThreshold = 0.75,
        [Description("Optional: Maximum number of similar memories to show (default: 5, max: 10).")] int similarLimit = 5,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.Get");

        // Add query details as Activity event
        activity?.AddEvent(new ActivityEvent("query.details", DateTimeOffset.UtcNow, new ActivityTagsCollection
        {
            {"query.id", id.ToString()},
            {"query.includeVersionHistory", includeVersionHistory.ToString()},
            {"query.versionNumber", versionNumber?.ToString() ?? "current"},
            {"query.includeArchivedRelationships", includeArchivedRelationships.ToString()}
        }));

        var memoryId = (MemoryId)id;

        // If requesting a specific version, get that version
        if (versionNumber.HasValue)
        {
            var version = await _storage.GetVersion(memoryId, new VersionNumber(versionNumber.Value), cancellationToken);
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
        Memory? memory = await _storage.Get(memoryId, cancellationToken);

        if (memory == null)
        {
            _logger.LogInformation("Memory not found for ID: {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Ok, "Memory not found");
            return $"Memory with ID {id} not found.";
        }

        // If includeArchivedRelationships is true, re-fetch relationships with archived targets
        if (includeArchivedRelationships)
        {
            memory.Relationships = await _storage.GetRelationships(memoryId, type: null, includeArchivedTargets: true, cancellationToken);
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
        result.AppendLine($"Owner: {FormatOwner(memory.Owner)}");
        result.AppendLine($"Archetype: {memory.Archetype.ToStringValue()}{(memory.Archetype.IsArchived() ? " ⚠️ (hidden from default searches)" : "")}");
        result.AppendLine($"Text: {memory.Text}");
        result.AppendLine($"Source: {memory.Source}");
        result.AppendLine(
            $"Tags: {(memory.Tags != null ? string.Join(", ", memory.Tags) : "none")}"
        );
        result.AppendLine($"Confidence: {memory.Confidence:F2}");
        result.AppendLine($"Current Version: {memory.CurrentVersion}");
        if (memory.Similarity.HasValue)
        {
            double percent = 100 * (double)memory.Similarity.Value;
            result.AppendLine($"Similarity: {percent:F1}%");
        }

        // Collect related memory IDs for suggestion
        var relatedMemoryIds = new HashSet<MemoryId>();

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
                var archivedIndicator = rel.TargetArchived ? " [ARCHIVED]" : "";

                result.AppendLine($"  • [{rel.Type.ToUpper()}] {direction} \"{relatedTitle}\" ({relatedType}) [ID: {relatedId}]{archivedIndicator}");

                // Collect related memory IDs (excluding the current memory)
                if (rel.FromMemoryId != memory.Id)
                    relatedMemoryIds.Add(rel.FromMemoryId);
                if (rel.ToMemoryId != memory.Id)
                    relatedMemoryIds.Add(rel.ToMemoryId);
            }
        }
        result.AppendLine($"Created: {memory.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        result.AppendLine($"Updated: {memory.UpdatedAt:yyyy-MM-dd HH:mm:ss}");

        // Add canonical URL if configured
        if (_canonicalUrlService.IsConfigured)
        {
            result.AppendLine($"URL: {_canonicalUrlService.GetMemoryUrl(memory.Id)}");
        }

        // Include version history if requested
        if (includeVersionHistory)
        {
            var limitClamped = Math.Clamp(versionLimit, 1, 20);
            var versions = await _storage.GetVersionHistory(memoryId, limitClamped, cancellationToken);

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

        // Include similar memories if requested (for consolidation discovery)
        var similarMemoryIds = new List<MemoryId>();
        if (includeSimilar)
        {
            var clampedSimilarLimit = Math.Clamp(similarLimit, 1, 10);
            var clampedThreshold = Math.Clamp(similarityThreshold, 0.0, 1.0);

            var similarMemories = await _storage.GetSimilarMemories(
                memoryId,
                new SimilarityScore(clampedThreshold),
                clampedSimilarLimit,
                cancellationToken);

            if (similarMemories.Count > 0)
            {
                result.AppendLine();
                result.AppendLine($"🔍 Similar Memories ({similarMemories.Count} found above {clampedThreshold * 100:F0}% similarity):");
                result.AppendLine("These may be candidates for consolidation, linking, or may provide additional context:");
                foreach (var similar in similarMemories)
                {
                    var percent = 100 * (double)similar.Similarity;
                    var relationshipIndicator = similar.HasExistingRelationship ? " [LINKED]" : "";
                    result.AppendLine($"  • \"{similar.Title}\" ({similar.Type}) - {percent:F1}% similar [ID: {similar.Id}]{relationshipIndicator}");
                    similarMemoryIds.Add(similar.Id);
                }
                result.AppendLine();
                result.AppendLine("💡 High similarity may indicate duplicate or overlapping content that could be consolidated.");
                result.AppendLine("Use GetMany to fetch full content of similar memories for comparison.");
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
        bool success = await _storage.Delete((MemoryId)id, cancellationToken);

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

        var memoryIds = ids.Select(id => (MemoryId)id).ToArray();
        var memories = await _storage.GetMany(memoryIds, cancellationToken);
        
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
        var relatedMemoryIdSet = new HashSet<MemoryId>();
        var inputIdSet = memoryIds.ToHashSet();

        foreach (var memory in memories)
        {
            result.AppendLine($"ID: {memory.Id}");
            if (memory.Title != null)
            {
                result.AppendLine($"Title: {memory.Title}");
            }
            result.AppendLine($"Type: {memory.Type}");
            result.AppendLine($"Owner: {FormatOwner(memory.Owner)}");
            result.AppendLine($"Archetype: {memory.Archetype.ToStringValue()}{(memory.Archetype.IsArchived() ? " ⚠️ (hidden from default searches)" : "")}");
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
                    if (rel.FromMemoryId != memory.Id && !inputIdSet.Contains(rel.FromMemoryId))
                        relatedMemoryIdSet.Add(rel.FromMemoryId);
                    if (rel.ToMemoryId != memory.Id && !inputIdSet.Contains(rel.ToMemoryId))
                        relatedMemoryIdSet.Add(rel.ToMemoryId);
                }
            }

            result.AppendLine($"Created: {memory.CreatedAt:yyyy-MM-dd HH:mm:ss}");

            // Add canonical URL if configured
            if (_canonicalUrlService.IsConfigured)
            {
                result.AppendLine($"URL: {_canonicalUrlService.GetMemoryUrl(memory.Id)}");
            }

            result.AppendLine();
        }

        // Add suggestion to load related memories if any exist
        if (relatedMemoryIdSet.Count > 0)
        {
            result.AppendLine("💡 Suggestion: These memories have relationships to other memories not included in this result.");
            result.AppendLine($"Consider using GetMany with these additional IDs to load more related context: [{string.Join(", ", relatedMemoryIdSet)}]");
            result.AppendLine("This can provide additional relevant information and context for your task.");
        }

        activity?.SetStatus(ActivityStatusCode.Ok, $"Retrieved {memories.Count} memories");
        return result.ToString();
    }

    [McpServerTool, Description("Create a reference (relationship) between two memories. Use this to link related reference materials, how-tos, or examples (e.g., 'example-of', 'explains', 'related-to'). References help organize knowledge for easier retrieval and understanding.")]
    public async Task<string> CreateReference(
        [Description("The ID of the source memory (e.g., the reference or how-to that is providing context)")] Guid fromId,
        [Description("The ID of the target memory (e.g., the example or related reference)")] Guid toId,
        [Description("The type of reference (e.g., 'example-of', 'explains', 'related-to'). Use references to connect and organize knowledge.")] string type,
        CancellationToken cancellationToken = default
    )
    {
        var rel = await _storage.CreateRelationship((MemoryId)fromId, (MemoryId)toId, type, cancellationToken);
        return $"Reference created: {rel.Id} from {rel.FromMemoryId} to {rel.ToMemoryId} (type: {rel.Type})";
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

        var memoryId = (MemoryId)id;
        var targetVersion = new VersionNumber(versionNumber);

        // First check the memory exists
        var existingMemory = await _storage.Get(memoryId, cancellationToken);
        if (existingMemory == null)
        {
            _logger.LogInformation("Cannot revert: Memory not found for ID: {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Ok, "Memory not found");
            return $"Memory with ID {id} not found. Cannot revert.";
        }

        // Check if already at the target version
        if ((int)existingMemory.CurrentVersion == versionNumber)
        {
            return $"Memory is already at version {versionNumber}. No changes made.";
        }

        // Perform the revert
        var revertedMemory = await _storage.RevertToVersion(memoryId, targetVersion, changedBy, cancellationToken);

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

    // ===== Archival Tools =====

    [McpServerTool, Description("Archive a memory, marking it as obsolete. Archived memories are hidden from default searches and relationship displays but preserved for historical reference and audit trails. Use this when consolidating memories or marking outdated content.")]
    public async Task<string> ArchiveMemory(
        [Description("The ID of the memory to archive.")] Guid id,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.ArchiveMemory");

        var memoryId = (MemoryId)id;

        // First check the memory exists
        var existingMemory = await _storage.Get(memoryId, cancellationToken);
        if (existingMemory == null)
        {
            _logger.LogInformation("Cannot archive: Memory not found for ID: {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Ok, "Memory not found");
            return $"Memory with ID {id} not found. Cannot archive.";
        }

        // Check if already archived
        if (existingMemory.Archetype == ArchetypeEnum.Archived)
        {
            return $"Memory {id} is already archived. No changes made.";
        }

        // Archive the memory
        var archivedMemory = await _storage.UpdateMemoryArchetypeAsync(memoryId, ArchetypeEnum.Archived, cancellationToken);

        if (archivedMemory == null)
        {
            _logger.LogWarning("Failed to archive memory {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Error, "Archive failed");
            return $"Failed to archive memory {id}.";
        }

        _logger.LogInformation("Memory {MemoryId} archived. Previous archetype: {OldArchetype}",
            id, existingMemory.Archetype.ToStringValue());

        activity?.SetStatus(ActivityStatusCode.Ok, "Memory archived successfully");

        StringBuilder result = new();
        result.AppendLine($"✅ Memory successfully archived.");
        result.AppendLine();
        result.AppendLine($"  ID: {archivedMemory.Id}");
        result.AppendLine($"  Title: {archivedMemory.Title ?? "Untitled"}");
        result.AppendLine($"  Previous Archetype: {existingMemory.Archetype.ToStringValue()}");
        result.AppendLine($"  New Archetype: archived");
        result.AppendLine();
        result.AppendLine("The memory is now hidden from default searches and relationship displays.");
        result.AppendLine("Use RestoreMemory to restore it, or ListArchived to view all archived memories.");

        return result.ToString();
    }

    [McpServerTool, Description("Restore an archived memory back to active status. The memory will become visible in searches and relationship displays again.")]
    public async Task<string> RestoreMemory(
        [Description("The ID of the archived memory to restore.")] Guid id,
        [Description("The archetype to restore to: 'document' for living, editable content or 'record' for historical, immutable records. Default is 'document'.")] string restoreAs = "document",
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.RestoreMemory");

        var memoryId = (MemoryId)id;

        // Parse the restore archetype
        var targetArchetype = ArchetypeEnumExtensions.ParseArchetype(restoreAs);
        if (targetArchetype == ArchetypeEnum.Archived)
        {
            return "Cannot restore to 'archived' status. Use ArchiveMemory instead if you want to archive the memory.";
        }

        // First check the memory exists
        var existingMemory = await _storage.Get(memoryId, cancellationToken);
        if (existingMemory == null)
        {
            _logger.LogInformation("Cannot restore: Memory not found for ID: {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Ok, "Memory not found");
            return $"Memory with ID {id} not found. Cannot restore.";
        }

        // Check if already active
        if (existingMemory.Archetype != ArchetypeEnum.Archived)
        {
            return $"Memory {id} is not archived (current archetype: {existingMemory.Archetype.ToStringValue()}). No changes made.";
        }

        // Restore the memory
        var restoredMemory = await _storage.UpdateMemoryArchetypeAsync(memoryId, targetArchetype, cancellationToken);

        if (restoredMemory == null)
        {
            _logger.LogWarning("Failed to restore memory {MemoryId}", id);
            activity?.SetStatus(ActivityStatusCode.Error, "Restore failed");
            return $"Failed to restore memory {id}.";
        }

        _logger.LogInformation("Memory {MemoryId} restored to archetype: {NewArchetype}",
            id, targetArchetype.ToStringValue());

        activity?.SetStatus(ActivityStatusCode.Ok, "Memory restored successfully");

        StringBuilder result = new();
        result.AppendLine($"✅ Memory successfully restored.");
        result.AppendLine();
        result.AppendLine($"  ID: {restoredMemory.Id}");
        result.AppendLine($"  Title: {restoredMemory.Title ?? "Untitled"}");
        result.AppendLine($"  Previous Archetype: archived");
        result.AppendLine($"  New Archetype: {targetArchetype.ToStringValue()}");
        result.AppendLine();
        result.AppendLine("The memory is now visible in searches and relationship displays.");

        return result.ToString();
    }

    [McpServerTool, Description("Filter memories by tags, type, project, or workspace. At least one filter (tags or type) is required. Tags use AND logic — all specified tags must be present. Results can be scoped to a specific project or workspace.")]
    public async Task<string> GetByFilter(
        [Description("One or more tags to filter by (case-insensitive, AND logic). Optional if type is provided.")] string[]? tags = null,
        [Description("Memory type to filter by (e.g. 'reference', 'how-to'). Optional if tags are provided.")] string? type = null,
        [Description("Page number (1-based). Default is 1.")] int page = 1,
        [Description("Number of results per page. Default is 20, max is 100.")] int pageSize = 20,
        [Description("Optional project ID to scope results to a specific project.")] string? projectId = null,
        [Description("Optional workspace ID to scope results to a specific workspace. Ignored if projectId is set.")] string? workspaceId = null,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.GetByFilter");

        // Filter out empty/whitespace tags
        var validTags = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];

        if (validTags.Length == 0 && string.IsNullOrWhiteSpace(type))
        {
            return "At least one filter is required: provide tags and/or type.";
        }

        // Validate pagination
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        // Determine optional owner scope
        MemoryOwner? owner = null;
        var parsedProjectId = ParseOptionalGuid(projectId);
        var parsedWorkspaceId = ParseOptionalGuid(workspaceId);

        if (parsedProjectId.HasValue)
        {
            owner = MemoryOwner.ForProject(new ProjectId(parsedProjectId.Value));
        }
        else if (parsedWorkspaceId.HasValue)
        {
            owner = MemoryOwner.ForWorkspace(new WorkspaceId(parsedWorkspaceId.Value));
        }

        List<Memorizer.Models.Memory> memories;
        int totalCount;

        if (validTags.Length > 0)
        {
            // Tag-based filtering (supports all additional filters)
            var result2 = await _storage.GetMemoriesByTagAsync(validTags, page, pageSize, owner, type, cancellationToken);
            memories = result2.Memories.ToList();
            totalCount = result2.TotalCount;
        }
        else if (owner.HasValue)
        {
            // Type + owner filtering (no tags)
            memories = (await _storage.GetMemoriesByOwnerAsync(owner.Value, page, pageSize, type, cancellationToken)).ToList();
            totalCount = await _storage.GetMemoryCountByOwnerAsync(owner.Value, type, cancellationToken);
        }
        else
        {
            // Type-only filtering
            var result2 = await _storage.GetMemoriesPaginated(page, pageSize, type, cancellationToken);
            memories = result2.Memories;
            totalCount = result2.TotalCount;
        }

        // Build filter description
        var filterParts = new List<string>();
        if (validTags.Length > 0) filterParts.Add($"tags: {string.Join(", ", validTags.Select(t => $"\"{t}\""))}");
        if (!string.IsNullOrWhiteSpace(type)) filterParts.Add($"type: \"{type}\"");
        var filterLabel = string.Join(", ", filterParts);

        _logger.LogInformation("GetByFilter completed. Filter: {Filter}, Page: {Page}, PageSize: {PageSize}, ResultCount: {ResultCount}, TotalCount: {TotalCount}",
            filterLabel, page, pageSize, memories.Count, totalCount);

        if (memories.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok, "No memories found for filter");
            var scopeInfo = owner.HasValue ? $" in {FormatOwner(owner.Value)}" : "";
            return $"No memories found with {filterLabel}{scopeInfo}.";
        }

        StringBuilder result = new();
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        result.AppendLine($"Filtered memories ({filterLabel}) — Page {page} of {totalPages}, {totalCount} total:");
        if (owner.HasValue)
        {
            result.AppendLine($"Scoped to: {FormatOwner(owner.Value)}");
        }
        result.AppendLine();

        var memoryIds = new List<Guid>();

        foreach (var memory in memories)
        {
            memoryIds.Add(memory.Id.Value);

            result.AppendLine($"ID: {memory.Id}");
            result.AppendLine($"  Title: {memory.Title ?? "Untitled"}");
            result.AppendLine($"  Type: {memory.Type}");
            result.AppendLine($"  Owner: {FormatOwner(memory.Owner)}");
            result.AppendLine($"  Tags: {(memory.Tags != null ? string.Join(", ", memory.Tags) : "none")}");
            result.AppendLine($"  Updated: {memory.UpdatedAt:yyyy-MM-dd HH:mm:ss}");

            if (_canonicalUrlService.IsConfigured)
            {
                result.AppendLine($"  URL: {_canonicalUrlService.GetMemoryUrl(memory.Id)}");
            }

            result.AppendLine();
        }

        if (totalPages > 1)
        {
            result.AppendLine("---");
            if (page < totalPages)
            {
                result.AppendLine($"Use GetByFilter with page={page + 1} to see more results.");
            }
            if (page > 1)
            {
                result.AppendLine($"Use GetByFilter with page={page - 1} to see previous results.");
            }
        }

        result.AppendLine();
        result.AppendLine($"Use Get with a memory ID to view full content.");
        result.AppendLine($"Use GetMany with IDs: [{string.Join(", ", memoryIds)}] to fetch all at once.");

        activity?.SetStatus(ActivityStatusCode.Ok, $"Listed {memories.Count} memories for filter {filterLabel}");
        return result.ToString();
    }

    [McpServerTool, Description("List archived memories with pagination. Use this to browse obsolete content for reference, audit, or potential restoration.")]
    public async Task<string> ListArchived(
        [Description("Page number (1-based). Default is 1.")] int page = 1,
        [Description("Number of results per page. Default is 20, max is 100.")] int pageSize = 20,
        [Description("Optional project ID to filter archived memories by project.")] string? projectId = null,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = TelemetryConfig.ActivitySource.StartActivity("MemoryTools.ListArchived");

        // Validate pagination
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var parsedProjectId = ParseOptionalGuid(projectId);
        ProjectId? typedProjectId = parsedProjectId.HasValue ? new ProjectId(parsedProjectId.Value) : null;

        var (memories, totalCount) = await _storage.GetArchivedMemoriesAsync(page, pageSize, typedProjectId, cancellationToken);

        _logger.LogInformation("ListArchived completed. Page: {Page}, PageSize: {PageSize}, ResultCount: {ResultCount}, TotalCount: {TotalCount}",
            page, pageSize, memories.Count, totalCount);

        if (memories.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok, "No archived memories found");
            if (parsedProjectId.HasValue)
            {
                return $"No archived memories found in project {parsedProjectId.Value}.";
            }
            return "No archived memories found.";
        }

        StringBuilder result = new();
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        result.AppendLine($"📦 Archived Memories (Page {page} of {totalPages}, {totalCount} total):");
        if (parsedProjectId.HasValue)
        {
            result.AppendLine($"Filtered by project: {parsedProjectId.Value}");
        }
        result.AppendLine();

        foreach (var memory in memories)
        {
            result.AppendLine($"ID: {memory.Id}");
            result.AppendLine($"  Title: {memory.Title ?? "Untitled"}");
            result.AppendLine($"  Type: {memory.Type}");
            result.AppendLine($"  Owner: {FormatOwner(memory.Owner)}");
            result.AppendLine($"  Tags: {(memory.Tags != null ? string.Join(", ", memory.Tags) : "none")}");
            result.AppendLine($"  Updated: {memory.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
            result.AppendLine();
        }

        if (totalPages > 1)
        {
            result.AppendLine("---");
            if (page < totalPages)
            {
                result.AppendLine($"Use ListArchived with page={page + 1} to see more results.");
            }
            if (page > 1)
            {
                result.AppendLine($"Use ListArchived with page={page - 1} to see previous results.");
            }
        }

        result.AppendLine();
        result.AppendLine("💡 Use RestoreMemory with a memory ID to restore it to active status.");
        result.AppendLine("💡 Use Get with a memory ID to view full content of an archived memory.");

        activity?.SetStatus(ActivityStatusCode.Ok, $"Listed {memories.Count} archived memories");
        return result.ToString();
    }

    /// <summary>
    /// Parses an optional Guid parameter defensively. MCP clients (e.g. Cursor, Ollama-based clients)
    /// may send empty strings or the literal string "null" instead of a proper JSON null for optional
    /// Guid fields. This helper treats those as null rather than throwing a deserialization exception.
    /// </summary>
    private static Guid? ParseOptionalGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    private static bool TryParseOptionalGuid(string? value, out Guid? guid)
    {
        guid = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Guid.TryParse(value, out var parsed)) return false;

        guid = parsed;
        return true;
    }
}
