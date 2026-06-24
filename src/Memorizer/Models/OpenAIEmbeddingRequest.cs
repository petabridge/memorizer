using System.Text.Json.Serialization;

namespace Memorizer.Models;

public class OpenAIEmbeddingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("input")]
    public string Input { get; init; } = string.Empty;
}

public class OpenAIEmbeddingResponse
{
    [JsonPropertyName("data")]
    public OpenAIEmbeddingData[] Data { get; init; } = [];
}

public class OpenAIEmbeddingData
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; init; } = [];

    [JsonPropertyName("index")]
    public int Index { get; init; }
}
