using System.Text.Json.Serialization;

namespace Memorizer.Models;

public class EmbeddingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;
}
