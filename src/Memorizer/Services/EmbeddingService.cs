using System.Text.Json;
using Memorizer.Models;
using Memorizer.Settings;

namespace Memorizer.Services;

public interface IEmbeddingService
{
    Task<float[]> Generate(
        string text,
        CancellationToken cancellationToken = default
    );

    Task<float[]> Generate(
        JsonDocument document,
        CancellationToken cancellationToken = default
    );
}

public class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly EmbeddingSettings _settings;
    private readonly IEmbeddingDimensionService _dimensionService;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        HttpClient httpClient,
        EmbeddingSettings settings,
        IEmbeddingDimensionService dimensionService,
        ILogger<EmbeddingService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _dimensionService = dimensionService;
        _logger = logger;
        _httpClient.Timeout = _settings.Timeout;
    }

    public async Task<float[]> Generate(
        string text,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Generating embedding for text of length {TextLength}", text.Length);

            EmbeddingRequest request = new() { Model = _settings.Model, Prompt = text };

            _logger.LogDebug("Sending request to embedding API at {ApiUrl}", _settings.ApiUrl);
            HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/embeddings",
                request,
                cancellationToken
            );
            response.EnsureSuccessStatusCode();

            EmbeddingResponse? result =
                await response.Content.ReadFromJsonAsync<EmbeddingResponse>(
                    cancellationToken: cancellationToken
                );
            if (result?.Embedding == null || result.Embedding.Length == 0)
            {
                throw new Exception("Failed to generate embedding: Empty response from API");
            }

            _logger.LogDebug("Successfully generated embedding with {Dimensions} dimensions", result.Embedding.Length);

            return result.Embedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embedding: {ErrorMessage}", ex.Message);

            // Fallback to a random embedding in case of error
            // Use effective dimensions from the dimension service (detected > stored > schema > 384)
            _logger.LogWarning("Falling back to random embedding generation");
            var dimensions = await _dimensionService.GetEffectiveDimensionsAsync(cancellationToken);

            Random random = new();
            float[] embedding = new float[dimensions];
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] = (float)random.NextDouble();
            }

            // Normalize the embedding
            float sum = 0;
            for (int i = 0; i < embedding.Length; i++)
            {
                sum += embedding[i] * embedding[i];
            }

            float magnitude = (float)Math.Sqrt(sum);
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= magnitude;
            }

            return embedding;
        }
    }

    public async Task<float[]> Generate(
        JsonDocument document,
        CancellationToken cancellationToken = default
    )
    {
        string jsonString = document.RootElement.ToString();
        return await Generate(jsonString, cancellationToken);
    }
}
