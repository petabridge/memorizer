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
/// Thrown when an embedding cannot be generated. This exception is deliberately
/// allowed to propagate to callers (e.g. StoreMemory) so that a failed embedding
/// request never results in a garbage/fallback vector being persisted, which would
/// silently corrupt semantic search for that row.
/// </summary>
public sealed class EmbeddingGenerationException : Exception
{
    public EmbeddingGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Embedding service that uses IOptionsSnapshot for reloadable configuration.
/// Register as Scoped to get fresh settings on each request scope.
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingApiClient _apiClient;
    private readonly IOptionsSnapshot<EmbeddingSettings> _settingsSnapshot;
    private readonly ILogger<EmbeddingService> _logger;

    private EmbeddingSettings Settings => _settingsSnapshot.Value;

    public EmbeddingService(
        IEmbeddingApiClient apiClient,
        IOptionsSnapshot<EmbeddingSettings> settingsSnapshot,
        ILogger<EmbeddingService> logger)
    {
        _apiClient = apiClient;
        _settingsSnapshot = settingsSnapshot;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate as cancellation, do NOT treat as an
            // embedding failure and do NOT substitute a fallback vector.
            throw;
        }
        catch (Exception ex)
        {
            // Fail loudly. Previously this method fell back to a random unit vector,
            // which was then persisted into the embedding/embedding_metadata columns,
            // silently poisoning that row for semantic search. We now surface the
            // failure so the caller (e.g. StoreMemory) aborts instead of storing garbage.
            // Note: we include the model name and input length for diagnostics but never
            // the full input text.
            _logger.LogError(
                ex,
                "Failed to generate embedding using model '{Model}' for text of length {TextLength}",
                Settings.Model,
                text.Length);

            throw new EmbeddingGenerationException(
                $"Failed to generate embedding using model '{Settings.Model}' for input of length {text.Length}. " +
                "Refusing to persist a fallback embedding, which would corrupt semantic search for this record.",
                ex);
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
