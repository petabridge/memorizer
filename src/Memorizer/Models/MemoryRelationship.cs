namespace Memorizer.Models;

public class MemoryRelationship
{
    public Guid Id { get; init; }
    public Guid FromMemoryId { get; init; }
    public Guid ToMemoryId { get; init; }
    public string Type { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public int? CreatedInVersion { get; init; }
    public int? DeletedInVersion { get; init; }
    public string? RelatedMemoryTitle { get; set; }
    public string? RelatedMemoryType { get; set; }

    /// <summary>
    /// Similarity score (0.0 to 1.0) for 'similar-to' relationships.
    /// Null for other relationship types.
    /// </summary>
    public double? Score { get; init; }
} 