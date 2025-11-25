using System.Text.Json;
using Pgvector;

namespace Memorizer.Models;

public class Memory
{
    public Guid Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public JsonDocument Content { get; init; } = JsonDocument.Parse("{}");
    public string Source { get; init; } = string.Empty;
    public Vector Embedding { get; init; } = new(new float[384]); // Default size - actual embeddings use configured dimensions
    public Vector? EmbeddingMetadata { get; init; } = null;
    public string[]? Tags { get; init; }
    public double Confidence { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? Title { get; init; }
    public string Text { get; init; } = string.Empty;
    public int CurrentVersion { get; init; } = 1;
    public double? Similarity { get; set; }
    public List<MemoryRelationship>? Relationships { get; set; }
}
