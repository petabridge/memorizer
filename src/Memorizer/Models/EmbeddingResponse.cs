using System.Text.Json.Serialization;

namespace Memorizer.Models;

public class EmbeddingResponse
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; init; } = [];
}
