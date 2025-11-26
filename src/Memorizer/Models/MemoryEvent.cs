using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memorizer.Models;

/// <summary>
/// Database entity for memory events. Maps directly to the memory_events table.
/// Use <see cref="Change"/> property to get the strongly-typed event data.
/// </summary>
public class MemoryEvent
{
    public Guid EventId { get; init; }
    public Guid MemoryId { get; init; }
    public int VersionNumber { get; init; }
    public string EventType { get; init; } = string.Empty;
    public JsonDocument EventData { get; init; } = JsonDocument.Parse("{}");
    public DateTime Timestamp { get; init; }
    public string? ChangedBy { get; init; }

    private MemoryChangeEvent? _change;

    /// <summary>
    /// Gets the strongly-typed change event. Lazily deserialized from EventData.
    /// </summary>
    [JsonIgnore]
    public MemoryChangeEvent Change => _change ??= DeserializeChange();

    /// <summary>
    /// Gets a human-readable description of this event. Serialized to JSON for UI consumption.
    /// </summary>
    public string DisplayText => Change.GetDisplayText();

    private MemoryChangeEvent DeserializeChange()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            var json = EventData.RootElement.GetRawText();

            return EventType switch
            {
                MemoryChangeEventTypes.MemoryCreated =>
                    JsonSerializer.Deserialize<MemoryCreatedEvent>(json, options) ?? new MemoryCreatedEvent(),

                MemoryChangeEventTypes.ContentUpdated =>
                    JsonSerializer.Deserialize<ContentUpdatedEvent>(json, options) ?? new ContentUpdatedEvent("", ""),

                MemoryChangeEventTypes.MetadataUpdated =>
                    JsonSerializer.Deserialize<MetadataUpdatedEvent>(json, options) ?? new MetadataUpdatedEvent(new Dictionary<string, MetadataChange>()),

                MemoryChangeEventTypes.RelationshipAdded =>
                    JsonSerializer.Deserialize<RelationshipAddedEvent>(json, options) ?? new RelationshipAddedEvent("", Guid.Empty),

                MemoryChangeEventTypes.RelationshipRemoved =>
                    JsonSerializer.Deserialize<RelationshipRemovedEvent>(json, options) ?? new RelationshipRemovedEvent("", Guid.Empty),

                MemoryChangeEventTypes.MemoryReverted =>
                    JsonSerializer.Deserialize<MemoryRevertedEvent>(json, options) ?? new MemoryRevertedEvent(0),

                _ => new UnknownChangeEvent(EventType, json)
            };
        }
        catch (JsonException)
        {
            return new UnknownChangeEvent(EventType, EventData.RootElement.GetRawText());
        }
    }

    /// <summary>
    /// Creates a MemoryEvent from a strongly-typed change event.
    /// </summary>
    public static MemoryEvent Create(
        Guid memoryId,
        int versionNumber,
        MemoryChangeEvent change,
        string? changedBy = null)
    {
        var (eventType, eventData) = change.Serialize();

        return new MemoryEvent
        {
            EventId = Guid.NewGuid(),
            MemoryId = memoryId,
            VersionNumber = versionNumber,
            EventType = eventType,
            EventData = eventData,
            Timestamp = DateTime.UtcNow,
            ChangedBy = changedBy
        };
    }
}

/// <summary>
/// Event type discriminator constants for database storage.
/// </summary>
public static class MemoryChangeEventTypes
{
    public const string MemoryCreated = "memory_created";
    public const string ContentUpdated = "content_updated";
    public const string MetadataUpdated = "metadata_updated";
    public const string RelationshipAdded = "relationship_added";
    public const string RelationshipRemoved = "relationship_removed";
    public const string MemoryReverted = "memory_reverted";
}

/// <summary>
/// Base type for all memory change events. Provides strongly-typed event data.
/// </summary>
public abstract record MemoryChangeEvent
{
    /// <summary>
    /// Gets a human-readable description of this change.
    /// </summary>
    public abstract string GetDisplayText();

    /// <summary>
    /// Serializes this event to a type discriminator and JSON document for database storage.
    /// </summary>
    public abstract (string EventType, JsonDocument EventData) Serialize();

    protected static JsonDocument ToJsonDocument<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        return JsonDocument.Parse(json);
    }
}

/// <summary>
/// Event recorded when a memory is first created.
/// </summary>
public record MemoryCreatedEvent : MemoryChangeEvent
{
    public override string GetDisplayText() => "Memory created";

    public override (string EventType, JsonDocument EventData) Serialize()
        => (MemoryChangeEventTypes.MemoryCreated, ToJsonDocument(new { }));
}

/// <summary>
/// Event recorded when memory content is updated.
/// </summary>
public record ContentUpdatedEvent(
    [property: JsonPropertyName("old_text")] string OldText,
    [property: JsonPropertyName("new_text")] string NewText
) : MemoryChangeEvent
{
    public override string GetDisplayText()
    {
        // Create a meaningful diff description
        if (string.IsNullOrEmpty(OldText) && !string.IsNullOrEmpty(NewText))
            return "Content added";

        if (!string.IsNullOrEmpty(OldText) && string.IsNullOrEmpty(NewText))
            return "Content removed";

        // Show a preview of the change
        var oldPreview = TruncateForDisplay(OldText, 40);
        var newPreview = TruncateForDisplay(NewText, 40);

        return $"Changed: \"{oldPreview}\" → \"{newPreview}\"";
    }

    private static string TruncateForDisplay(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return "(empty)";

        // Normalize whitespace for display
        var normalized = text.Replace("\n", " ").Replace("\r", "").Trim();

        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..(maxLength - 3)] + "...";
    }

    public override (string EventType, JsonDocument EventData) Serialize()
        => (MemoryChangeEventTypes.ContentUpdated, ToJsonDocument(new { old_text = OldText, new_text = NewText }));
}

/// <summary>
/// Represents a change to a single metadata field.
/// </summary>
public record MetadataChange(
    [property: JsonPropertyName("old_value")] object? OldValue,
    [property: JsonPropertyName("new_value")] object? NewValue
);

/// <summary>
/// Event recorded when memory metadata (title, type, tags, confidence) is updated.
/// </summary>
public record MetadataUpdatedEvent(
    [property: JsonPropertyName("changes")] Dictionary<string, MetadataChange> Changes
) : MemoryChangeEvent
{
    public override string GetDisplayText()
    {
        if (Changes.Count == 0)
            return "Metadata updated";

        var fieldNames = Changes.Keys.ToList();

        if (fieldNames.Count == 1)
        {
            var field = fieldNames[0];
            var change = Changes[field];
            return $"Updated {field}: \"{change.OldValue}\" → \"{change.NewValue}\"";
        }

        return $"Updated {string.Join(", ", fieldNames)}";
    }

    public override (string EventType, JsonDocument EventData) Serialize()
        => (MemoryChangeEventTypes.MetadataUpdated, ToJsonDocument(new { changes = Changes }));
}

/// <summary>
/// Event recorded when a relationship is added to a memory.
/// </summary>
public record RelationshipAddedEvent(
    [property: JsonPropertyName("relationship_type")] string RelationshipType,
    [property: JsonPropertyName("target_memory_id")] Guid TargetMemoryId,
    [property: JsonPropertyName("target_memory_title")] string? TargetMemoryTitle = null
) : MemoryChangeEvent
{
    public override string GetDisplayText()
    {
        var target = !string.IsNullOrEmpty(TargetMemoryTitle)
            ? $"\"{TargetMemoryTitle}\""
            : TargetMemoryId.ToString()[..8];

        return $"Added {RelationshipType} → {target}";
    }

    public override (string EventType, JsonDocument EventData) Serialize()
        => (MemoryChangeEventTypes.RelationshipAdded, ToJsonDocument(new
        {
            relationship_type = RelationshipType,
            target_memory_id = TargetMemoryId,
            target_memory_title = TargetMemoryTitle
        }));
}

/// <summary>
/// Event recorded when a relationship is removed from a memory.
/// </summary>
public record RelationshipRemovedEvent(
    [property: JsonPropertyName("relationship_type")] string RelationshipType,
    [property: JsonPropertyName("target_memory_id")] Guid TargetMemoryId,
    [property: JsonPropertyName("target_memory_title")] string? TargetMemoryTitle = null
) : MemoryChangeEvent
{
    public override string GetDisplayText()
    {
        var target = !string.IsNullOrEmpty(TargetMemoryTitle)
            ? $"\"{TargetMemoryTitle}\""
            : TargetMemoryId.ToString()[..8];

        return $"Removed {RelationshipType} → {target}";
    }

    public override (string EventType, JsonDocument EventData) Serialize()
        => (MemoryChangeEventTypes.RelationshipRemoved, ToJsonDocument(new
        {
            relationship_type = RelationshipType,
            target_memory_id = TargetMemoryId,
            target_memory_title = TargetMemoryTitle
        }));
}

/// <summary>
/// Event recorded when a memory is reverted to a previous version.
/// </summary>
public record MemoryRevertedEvent(
    [property: JsonPropertyName("reverted_to_version")] int RevertedToVersion,
    [property: JsonPropertyName("reverted_from_version")] int RevertedFromVersion = 0
) : MemoryChangeEvent
{
    public override string GetDisplayText()
    {
        if (RevertedFromVersion > 0)
            return $"Reverted from v{RevertedFromVersion} to v{RevertedToVersion}";

        return $"Reverted to version {RevertedToVersion}";
    }

    public override (string EventType, JsonDocument EventData) Serialize()
        => (MemoryChangeEventTypes.MemoryReverted, ToJsonDocument(new
        {
            reverted_to_version = RevertedToVersion,
            reverted_from_version = RevertedFromVersion
        }));
}

/// <summary>
/// Fallback event type for unknown or legacy event types.
/// </summary>
public record UnknownChangeEvent(string EventType, string RawJson) : MemoryChangeEvent
{
    public override string GetDisplayText() => EventType;

    public override (string EventType, JsonDocument EventData) Serialize()
        => (EventType, JsonDocument.Parse(RawJson));
}
