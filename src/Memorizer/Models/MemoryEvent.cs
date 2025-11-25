using System.Text.Json;

namespace Memorizer.Models;

public class MemoryEvent
{
    public Guid EventId { get; init; }
    public Guid MemoryId { get; init; }
    public int VersionNumber { get; init; }
    public string EventType { get; init; } = string.Empty;
    public JsonDocument EventData { get; init; } = JsonDocument.Parse("{}");
    public DateTime Timestamp { get; init; }
    public string? ChangedBy { get; init; }

    public string GetDisplayText()
    {
        return EventType switch
        {
            "memory_created" => "Memory created",
            "content_updated" => "Content updated",
            "content_appended" => "Content appended",
            "section_updated" => "Section updated",
            "metadata_updated" => FormatMetadataChanges(),
            "relationship_added" => FormatRelationshipAdded(),
            "relationship_removed" => FormatRelationshipRemoved(),
            "memory_reverted" => FormatReverted(),
            _ => EventType
        };
    }

    private string FormatMetadataChanges()
    {
        try
        {
            var changes = new List<string>();
            var root = EventData.RootElement;

            if (root.TryGetProperty("changes", out var changesElement))
            {
                foreach (var prop in changesElement.EnumerateObject())
                {
                    changes.Add(prop.Name);
                }
            }

            return changes.Count > 0
                ? $"Updated {string.Join(", ", changes)}"
                : "Metadata updated";
        }
        catch
        {
            return "Metadata updated";
        }
    }

    private string FormatRelationshipAdded()
    {
        try
        {
            var root = EventData.RootElement;
            var relType = root.TryGetProperty("relationship_type", out var rt)
                ? rt.GetString()
                : "relationship";
            return $"Added {relType} relationship";
        }
        catch
        {
            return "Relationship added";
        }
    }

    private string FormatRelationshipRemoved()
    {
        try
        {
            var root = EventData.RootElement;
            var relType = root.TryGetProperty("relationship_type", out var rt)
                ? rt.GetString()
                : "relationship";
            return $"Removed {relType} relationship";
        }
        catch
        {
            return "Relationship removed";
        }
    }

    private string FormatReverted()
    {
        try
        {
            var root = EventData.RootElement;
            var targetVersion = root.TryGetProperty("reverted_to_version", out var v)
                ? v.GetInt32()
                : 0;
            return targetVersion > 0
                ? $"Reverted to version {targetVersion}"
                : "Reverted to previous version";
        }
        catch
        {
            return "Reverted to previous version";
        }
    }
}

public static class MemoryEventTypes
{
    public const string MemoryCreated = "memory_created";
    public const string ContentUpdated = "content_updated";
    public const string ContentAppended = "content_appended";
    public const string SectionUpdated = "section_updated";
    public const string MetadataUpdated = "metadata_updated";
    public const string RelationshipAdded = "relationship_added";
    public const string RelationshipRemoved = "relationship_removed";
    public const string MemoryReverted = "memory_reverted";
}
