using System.Text.Json;

namespace Memorizer.Models;

public class MemoryVersion
{
    public Guid VersionId { get; init; }
    public Guid MemoryId { get; init; }
    public int VersionNumber { get; init; }
    public string Type { get; init; } = string.Empty;
    public JsonDocument Content { get; init; } = JsonDocument.Parse("{}");
    public string Text { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string[]? Tags { get; init; }
    public double Confidence { get; init; }
    public string? Title { get; init; }
    public Guid[] RelationshipIds { get; init; } = Array.Empty<Guid>();
    public DateTime CreatedAt { get; init; }
    public DateTime VersionedAt { get; init; }

    public List<MemoryEvent>? Events { get; set; }
}
