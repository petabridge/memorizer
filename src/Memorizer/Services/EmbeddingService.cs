using System.Text.Json;
using Memorizer.Settings;
using Microsoft.Extensions.Options;

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

/// <summary>
/// Embedding service that uses IOptionsSnapshot for reloadable configuration.
/// Register as Scoped to get fresh settings on each request scope.
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingApiClient _apiClient;
    private readonly IOptionsSnapshot<EmbeddingSettings> _settingsSnapshot;
    private readonly IEmbeddingDimensionService _dimensionService;
    private readonly ILogger<EmbeddingService> _logger;

    private EmbeddingSettings Settings => _settingsSnapshot.Value;

    public EmbeddingService(
        IEmbeddingApiClient apiClient,
        IOptionsSnapshot<EmbeddingSettings> settingsSnapshot,
        IEmbeddingDimensionService dimensionService,
        ILogger<EmbeddingService> logger)
    {
        _apiClient = apiClient;
        _settingsSnapshot = settingsSnapshot;
        _dimensionService = dimensionService;
        _logger = logger;
    }

    public async Task<float[]> Generate(
        string text,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Generating embedding for text of length {TextLength}", text.Length);

            var embedding = await _apiClient.GenerateAsync(Settings.Model, text, cancellationToken);

            _logger.LogDebug("Successfully generated embedding with {Dimensions} dimensions", embedding.Length);

            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embedding: {ErrorMessage}", ex.Message);

            _logger.LogWarning("Falling back to random embedding generation");
            var dimensions = await _dimensionService.GetEffectiveDimensionsAsync(cancellationToken);

            Random random = new();
            float[] embedding = new float[dimensions];
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] = (float)random.NextDouble();
            }

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
